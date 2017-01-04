﻿//
//  CouchbaseLiteTcpContext.cs
//
//  Author:
//  	Jim Borden  <jim.borden@couchbase.com>
//
//  Copyright (c) 2015 Couchbase, Inc All rights reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using WebSocketSharp.Net;

namespace Couchbase.Lite.Listener.Tcp
{

    /// <summary>
    /// An implementation of CouchbaseListenerContext for TCP/IP
    /// </summary>
    internal sealed class CouchbaseListenerTcpContext : CouchbaseListenerContext
    {
        
        #region Variables

        private readonly HttpListenerRequest _request;
        private readonly HttpListenerResponse _response;

        #endregion

        #region Properties

        public override Stream BodyStream
        {
            get {
                return _request.InputStream;
            }
        }

        public override NameValueCollection RequestHeaders
        {
            get {
                return _request.Headers;
            }
        }

        public override long ContentLength
        {
            get {
                return _request.ContentLength64;
            }
        }

        public override string Method {
            get {
                return _request.HttpMethod;
            }
        }

        public override Uri RequestUrl {
            get {
                //Why I have to do this here again is beyond me...I'm actively fighting
                //against .NET to keep the %2F entities from turning into slashes
                return new Uri(_request.Url.OriginalString.Replace("%2F", "%252F"));
            }
        }

        #endregion

        #region Constructors

        public CouchbaseListenerTcpContext(HttpListenerRequest request, HttpListenerResponse response, Manager manager) : base(manager)
        {
            _request = request;
            _response = response;
        }

        #endregion

        #region ICouchbaseListenerContext

        public override string GetQueryParam(string key)
        {
            return _request.QueryString[key];
        }

        public override IDictionary<string, object> GetQueryParams()
        {
            var retVal = new Dictionary<string, object>(_request.QueryString.Count);
            foreach (string key in _request.QueryString.AllKeys) {
                retVal[key] = _request.QueryString[key];
            }

            return retVal;
        }

        public override bool CacheWithEtag(string etag)
        {
            etag = String.Format("\"{0}\"", etag);
            _response.Headers["Etag"] = etag;
            return etag.Equals(RequestHeaders.Get("If-None-Match"));
        }

        public override CouchbaseLiteResponse CreateResponse(StatusCode code = StatusCode.Ok)
        {
            return new CouchbaseLiteResponse(Method, RequestHeaders, new TcpResponseWriter(_response)) {
                InternalStatus = code
            };
        }

        #endregion
    }
}

