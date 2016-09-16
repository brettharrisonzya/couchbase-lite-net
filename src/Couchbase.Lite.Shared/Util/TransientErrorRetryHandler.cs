﻿using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using System.Net;
using Couchbase.Lite.Auth;

namespace Couchbase.Lite.Util
{
    internal sealed class TransientErrorRetryHandler : DelegatingHandler
    {
        private static readonly string Tag = typeof(TransientErrorRetryHandler).Name;
        private readonly IRetryStrategy _retryStrategy;

        public TransientErrorRetryHandler(HttpMessageHandler handler, IRetryStrategy strategy) : base(handler) 
        { 
            InnerHandler = handler;
            _retryStrategy = strategy;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var executor = new RetryStrategyExecutor(request, _retryStrategy, cancellationToken);
            executor.Send = ResendHandler;
            return ResendHandler(request, executor);
        }

        private Task<HttpResponseMessage> ResendHandler(HttpRequestMessage request, RetryStrategyExecutor executor)
        {
            return base.SendAsync(request, executor.Token)
                .ContinueWith(t => HandleTransientErrors(t, executor), executor.Token)
                .Unwrap();
        }

       
        static Task<HttpResponseMessage> HandleTransientErrors(Task<HttpResponseMessage> request, object state)
        {
            var executor = (RetryStrategyExecutor)state;
            if (!request.IsFaulted) 
            {
                var response = request.Result;
                if (executor.CanContinue && Misc.IsTransientError(response)) {
                    Log.To.Sync.V(Tag, "Retrying after transient error...");
                    return executor.Retry();
                }

                if (!response.IsSuccessStatusCode) {
                    Log.To.Sync.V(Tag, "Non transient error received ({0}), throwing HttpResponseException", 
                        response.StatusCode);

                    var exception = new HttpResponseException(response.StatusCode);
                    if(response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode ==
                        HttpStatusCode.ProxyAuthenticationRequired) {
                        var responseChallenge = response.Headers.WwwAuthenticate;
                        foreach(var header in responseChallenge) {
                            var challenge = AuthUtils.ParseAuthHeader(header);
                            if(challenge != null) {
                                exception.Data["AuthChallenge"] = challenge;
                            }
                        }
                    }

                    throw exception;
                }

                // If it's not faulted, there's nothing here to do.
                return request;
            }

            string statusCode;
            if (!Misc.IsTransientNetworkError(request.Exception, out statusCode) || !executor.CanContinue)
            {
                if (!executor.CanContinue) {
                    Log.To.Sync.V(Tag, "Out of retries for error, throwing", request.Exception);
                } else {
                    Log.To.Sync.V(Tag, "Non transient error received (status), throwing", request.Exception);
                }

                // If it's not transient, pass the exception along
                // for any other handlers to respond to.
                throw request.Exception;
            }


            // Retry again.
            Log.To.Sync.V(Tag, "Retrying after transient error...");
            return executor.Retry();
        }
    }
}

