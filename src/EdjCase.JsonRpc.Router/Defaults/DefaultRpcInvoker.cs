﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EdjCase.JsonRpc.Core;
using EdjCase.JsonRpc.Router.Abstractions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using EdjCase.JsonRpc.Router.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using EdjCase.JsonRpc.Router;
using System.IO;

namespace EdjCase.JsonRpc.Router.Defaults
{
	/// <summary>
	/// Default Rpc method invoker that uses asynchronous processing
	/// </summary>
	public class DefaultRpcInvoker : IRpcInvoker
	{
		/// <summary>
		/// Logger for logging Rpc invocation
		/// </summary>
		private ILogger<DefaultRpcInvoker> logger { get; }

		/// <summary>
		/// AspNet service to authorize requests
		/// </summary>
		private IAuthorizationService authorizationService { get; }
		/// <summary>
		/// Provides authorization policies for the authroziation service
		/// </summary>
		private IAuthorizationPolicyProvider policyProvider { get; }

		/// <summary>
		/// Configuration data for the server
		/// </summary>
		private IOptions<RpcServerConfiguration> serverConfig { get; }

		/// <summary>
		/// Matches the route method name and parameters to the correct method to execute
		/// </summary>
		private IRpcRequestMatcher rpcRequestMatcher { get; }

		private ConcurrentDictionary<Type, ObjectFactory> objectFactoryCache { get; } = new ConcurrentDictionary<Type, ObjectFactory>();
		private ConcurrentDictionary<Type, (List<IAuthorizeData>, bool)> classAttributeCache { get; } = new ConcurrentDictionary<Type, (List<IAuthorizeData>, bool)>();
		private ConcurrentDictionary<RpcMethodInfo, (List<IAuthorizeData>, bool)> methodAttributeCache { get; } = new ConcurrentDictionary<RpcMethodInfo, (List<IAuthorizeData>, bool)>();


		/// <param name="authorizationService">Service that authorizes each method for use if configured</param>
		/// <param name="policyProvider">Provides authorization policies for the authroziation service</param>
		/// <param name="logger">Optional logger for logging Rpc invocation</param>
		/// <param name="serverConfig">Configuration data for the server</param>
		/// <param name="rpcRequestMatcher">Matches the route method name and parameters to the correct method to execute</param>
		public DefaultRpcInvoker(IAuthorizationService authorizationService, IAuthorizationPolicyProvider policyProvider,
			ILogger<DefaultRpcInvoker> logger, IOptions<RpcServerConfiguration> serverConfig,
			IRpcRequestMatcher rpcRequestMatcher)
		{
			this.authorizationService = authorizationService;
			this.policyProvider = policyProvider;
			this.logger = logger;
			this.serverConfig = serverConfig;
			this.rpcRequestMatcher = rpcRequestMatcher;
		}


		/// <summary>
		/// Call the incoming Rpc requests methods and gives the appropriate respones
		/// </summary>
		/// <param name="requests">List of Rpc requests</param>
		/// <param name="routeContext">The context of the current request</param>
		/// <param name="path">Rpc path that applies to the current request</param>
		/// <returns>List of Rpc responses for the requests</returns>
		public async Task<List<RpcResponse>> InvokeBatchRequestAsync(IList<RpcRequest> requests, IRouteContext routeContext, RpcPath path = null)
		{
			this.logger.InvokingBatchRequests(requests.Count);
			var invokingTasks = new List<Task<RpcResponse>>();
			foreach (RpcRequest request in requests)
			{
				Task<RpcResponse> invokingTask = this.InvokeRequestAsync(request, routeContext, path);
				if (request.Id.HasValue)
				{
					//Only wait for non-notification requests
					invokingTasks.Add(invokingTask);
				}
			}

			await Task.WhenAll(invokingTasks.ToArray());

			List<RpcResponse> responses = invokingTasks
				.Select(t => t.Result)
				.Where(r => r != null)
				.ToList();

			this.logger.BatchRequestsComplete();

			return responses;
		}

		/// <summary>
		/// Call the incoming Rpc request method and gives the appropriate response
		/// </summary>
		/// <param name="request">Rpc request</param>
		/// <param name="path">Rpc path that applies to the current request</param>
		/// <param name="routeContext">The context of the current rpc request</param>
		/// <returns>An Rpc response for the request</returns>
		public async Task<RpcResponse> InvokeRequestAsync(RpcRequest request, IRouteContext routeContext, RpcPath path = null)
		{
			if (request == null)
			{
				throw new ArgumentNullException(nameof(request));
			}

			this.logger.InvokingRequest(request.Id);
			RpcResponse rpcResponse;
			try
			{
				if (!routeContext.MethodProvider.TryGetByPath(path, out IReadOnlyList<MethodInfo> methods))
				{
					throw new RpcException(RpcErrorCode.MethodNotFound, $"No methods found with the path: {path}");
				}
				Router.RpcMethodInfo rpcMethod = this.rpcRequestMatcher.GetMatchingMethod(request, methods);

				bool isAuthorized = await this.IsAuthorizedAsync(rpcMethod, routeContext);

				if (isAuthorized)
				{

					this.logger.InvokeMethod(request.Method);
					object result = await this.InvokeAsync(rpcMethod, path, routeContext.RequestServices);
					this.logger.InvokeMethodComplete(request.Method);

					if (result is IRpcMethodResult methodResult)
					{
						rpcResponse = methodResult.ToRpcResponse(request.Id);
					}
					else
					{
						rpcResponse = new RpcResponse(request.Id, result);
					}
				}
				else
				{
					var authError = new RpcError(RpcErrorCode.InvalidRequest, "Unauthorized");
					rpcResponse = new RpcResponse(request.Id, authError);
				}
			}
			catch (Exception ex)
			{
				const string errorMessage = "An Rpc error occurred while trying to invoke request.";
				this.logger.LogException(ex, errorMessage);
				RpcError error;
				if (ex is RpcException rpcException)
				{
					error = rpcException.ToRpcError(this.serverConfig.Value.ShowServerExceptions);
				}
				else
				{
					error = new RpcError(RpcErrorCode.InternalError, errorMessage, ex);
				}
				rpcResponse = new RpcResponse(request.Id, error);
			}

			if (request.Id.HasValue)
			{
				this.logger.FinishedRequest(request.Id.ToString());
				//Only give a response if there is an id
				return rpcResponse;
			}
			//TODO make no id run in a non-blocking way
			this.logger.FinishedRequestNoId();
			return null;
		}

		private async Task<bool> IsAuthorizedAsync(RpcMethodInfo methodInfo, IRouteContext routeContext)
		{
			(List<IAuthorizeData> authorizeDataListClass, bool allowAnonymousOnClass) = this.classAttributeCache.GetOrAdd(methodInfo.Method.DeclaringType, GetClassAttributeInfo);
			(List<IAuthorizeData> authorizeDataListMethod, bool allowAnonymousOnMethod) = this.methodAttributeCache.GetOrAdd(methodInfo, GetMethodAttributeInfo);

			if (authorizeDataListClass.Any() || authorizeDataListMethod.Any())
			{
				if (allowAnonymousOnClass || allowAnonymousOnMethod)
				{
					this.logger.SkippingAuth();
				}
				else
				{
					this.logger.RunningAuth();
					AuthorizationResult authResult = await this.CheckAuthorize(authorizeDataListClass, routeContext);
					if (authResult.Succeeded)
					{
						//Have to pass both controller and method authorize
						authResult = await this.CheckAuthorize(authorizeDataListMethod, routeContext);
					}
					if (authResult.Succeeded)
					{
						this.logger.AuthSuccessful();
					}
					else
					{
						this.logger.AuthFailed();
						return false;
					}
				}
			}
			else
			{
				this.logger.NoConfiguredAuth();
			}
			return true;

			//functions
			(List<IAuthorizeData> Data, bool allowAnonymous) GetClassAttributeInfo(Type type)
			{
				return GetAttributeInfo(type.GetCustomAttributes());
			}

			(List<IAuthorizeData> Data, bool allowAnonymous) GetMethodAttributeInfo(RpcMethodInfo info)
			{
				return GetAttributeInfo(info.Method.GetCustomAttributes());
			}
			(List<IAuthorizeData> Data, bool allowAnonymous) GetAttributeInfo(IEnumerable<Attribute> attributes)
			{
				bool allowAnonymous = false;
				var dataList = new List<IAuthorizeData>(10);
				foreach (Attribute attribute in attributes)
				{
					if (attribute is IAuthorizeData data)
					{
						dataList.Add(data);
					}
					if (!allowAnonymous && attribute is IAllowAnonymous)
					{
						allowAnonymous = true;
					}
				}
				return (dataList, allowAnonymous);
			}
		}

		private async Task<AuthorizationResult> CheckAuthorize(List<IAuthorizeData> authorizeDataList, IRouteContext routeContext)
		{
			if (!authorizeDataList.Any())
			{
				return AuthorizationResult.Success();
			}
			AuthorizationPolicy policy = await AuthorizationPolicy.CombineAsync(this.policyProvider, authorizeDataList);
			return await this.authorizationService.AuthorizeAsync(routeContext.User, policy);
		}





		/// <summary>
		/// Invokes the method with the specified parameters, returns the result of the method
		/// </summary>
		/// <exception cref="RpcInvalidParametersException">Thrown when conversion of parameters fails or when invoking the method is not compatible with the parameters</exception>
		/// <param name="parameters">List of parameters to invoke the method with</param>
		/// <returns>The result of the invoked method</returns>
		private async Task<object> InvokeAsync(Router.RpcMethodInfo methodInfo, RpcPath path, IServiceProvider serviceProvider)
		{
			object obj = null;
			if (serviceProvider != null)
			{
				//Use service provider (if exists) to create instance
				ObjectFactory objectFactory = this.objectFactoryCache.GetOrAdd(methodInfo.Method.DeclaringType, (t) => ActivatorUtilities.CreateFactory(t, new Type[0]));
				obj = objectFactory(serviceProvider, null);
			}
			if (obj == null)
			{
				//Use reflection to create instance if service provider failed or is null
				obj = Activator.CreateInstance(methodInfo.Method.DeclaringType);
			}
			try
			{
				object returnObj = methodInfo.Method.Invoke(obj, methodInfo.Parameters);

				returnObj = await DefaultRpcInvoker.HandleAsyncResponses(returnObj);

				return returnObj;
			}
			catch (TargetInvocationException ex)
			{
				var routeInfo = new RpcRouteInfo(methodInfo, path, serviceProvider);

				//Controller error handling
				RpcErrorFilterAttribute errorFilter = methodInfo.Method.DeclaringType.GetTypeInfo().GetCustomAttribute<RpcErrorFilterAttribute>();
				if (errorFilter != null)
				{
					OnExceptionResult result = errorFilter.OnException(routeInfo, ex.InnerException);
					if (!result.ThrowException)
					{
						return result.ResponseObject;
					}
					if (result.ResponseObject is Exception rEx)
					{
						throw rEx;
					}
				}
				throw new RpcException(RpcErrorCode.InternalError, "Exception occurred from target method execution.", ex);
			}
			catch (Exception ex)
			{
				throw new RpcException(RpcErrorCode.InvalidParams, "Exception from attempting to invoke method. Possibly invalid parameters for method.", ex);
			}
		}

		/// <summary>
		/// Handles/Awaits the result object if it is a async Task
		/// </summary>
		/// <param name="returnObj">The result of a invoked method</param>
		/// <returns>Awaits a Task and returns its result if object is a Task, otherwise returns the same object given</returns>
		private static async Task<object> HandleAsyncResponses(object returnObj)
		{
			Task task = returnObj as Task;
			if (task == null) //Not async request
			{
				return returnObj;
			}
			try
			{
				await task;
			}
			catch (Exception ex)
			{
				throw new TargetInvocationException(ex);
			}
			PropertyInfo propertyInfo = task.GetType().GetProperty("Result");
			if (propertyInfo != null)
			{
				//Type of Task<T>. Wait for result then return it
				return propertyInfo.GetValue(returnObj);
			}
			//Just of type Task with no return result			
			return null;
		}
	}
}
