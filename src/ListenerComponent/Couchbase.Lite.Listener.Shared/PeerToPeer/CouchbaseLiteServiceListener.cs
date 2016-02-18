﻿//
//  CouchbaseLiteServiceListener.cs
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
using Couchbase.Lite.Util;
using System.Security;
using Sharpen;
using System.Runtime.InteropServices;
using System.Text;
using Couchbase.Lite.Listener.Tcp;
using System.Net.Http.Headers;

namespace Couchbase.Lite.Listener
{

    /// <summary>
    /// An abstract base class for Listening for a Couchbase Lite P2P connection
    /// </summary>
    public abstract class CouchbaseLiteServiceListener : IDisposable
    {

        #region Variables

        internal readonly CouchbaseLiteRouter _router = new CouchbaseLiteRouter();
        private bool _disposed;
        private Dictionary<string, SecureString> _passwordMap = new Dictionary<string, SecureString>();

        #endregion

        #region Properties

        /// <summary>
        /// Whether or not this listener is operating in read-only mode (i.e. no changes to databases
        /// are permitted)
        /// </summary>
        public bool ReadOnly {
            get {
                return _readOnly;
            }
            set {
                if (value) {
                    _router.OnAccessCheck = (method, endpoint) =>
                    {
                        if(method.Equals("HEAD") || method.Equals("GET")) {
                            return new Status(StatusCode.Ok);
                        } 
                        if(method.Equals("POST") && (endpoint.EndsWith("_all_docs") || endpoint.EndsWith("_revs_diff"))) {
                            return new Status(StatusCode.Ok);
                        }

                        return new Status(StatusCode.Forbidden);
                    };
                } else {
                    _router.OnAccessCheck = null;
                }
                _readOnly = value;
            }
        }
        private bool _readOnly;

        /// <summary>
        /// Gets a value indicating whether this <see cref="Couchbase.Lite.Listener.CouchbaseLiteServiceListener"/>
        /// requires authentication for access.
        /// </summary>
        protected bool RequiresAuth { get; private set; }

        #endregion

        #region Public Methods

        /// <summary>
        /// Start listening and processing requests
        /// </summary>
        public abstract void Start();

        /// <summary>
        /// Stop listening and processing requests, but handle
        /// the currently received ones
        /// </summary>
        public abstract void Stop();

        /// <summary>
        /// Stop listening and processing requests immediately
        /// </summary>
        public abstract void Abort();

        /// <summary>
        /// Sets up passwords for HTTP authentication
        /// </summary>
        /// <param name="usersAndPasswords">A dictionary containing the users and their passwords</param>
        public void SetPasswords(IDictionary<string, string> usersAndPasswords)
        {
            _passwordMap.Clear();
            if (usersAndPasswords == null) {
                RequiresAuth = false;
                return;
            }

            foreach (var pair in usersAndPasswords) {
                var secureString = new SecureString();
                foreach (var c in pair.Value) {
                    secureString.AppendChar(c);
                }

                secureString.MakeReadOnly();
                _passwordMap[pair.Key] = secureString;
            }

            RequiresAuth = _passwordMap.Count > 0;
        }

        /// <summary>
        /// Sets up passwords for HTTP authentication
        /// </summary>
        /// <param name="usersAndPasswords">A dictionary containing the users and their passwords</param>
        public void SetPasswords(IDictionary<string, SecureString> usersAndPasswords)
        {
            _passwordMap.Clear();
            if (usersAndPasswords == null) {
                RequiresAuth = false;
                return;
            }

            _passwordMap = new Dictionary<string, SecureString>();
            foreach (var pair in usersAndPasswords) {
                pair.Value.MakeReadOnly();
                _passwordMap[pair.Key] = pair.Value;
            }

            RequiresAuth = _passwordMap.Count > 0;
        }

        #endregion

        #region Protected Methods

        /// <summary>
        /// Used by subclasses to dispose resources
        /// </summary>
        protected virtual void DisposeInternal() {}

        /// <summary>
        /// Validates the user (HTTP Basic).
        /// </summary>
        /// <returns><c>true</c>, if user was validated, <c>false</c> otherwise.</returns>
        /// <param name="headerValue">The header value received from the HTTP request</param>
        protected bool ValidateUser(string headerValue)
        {
            var parsed = AuthenticationHeaderValue.Parse(headerValue);
            if (parsed.Scheme != "Basic") {
                return false;
            }

            var userAndPassStr = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter));
            var firstColon = userAndPassStr.IndexOf(':');
            if (firstColon == -1) {
                return false;
            }

            var user = userAndPassStr.Substring(0, firstColon);
            var pass = Encoding.UTF8.GetBytes(userAndPassStr.Substring(firstColon + 1));
            userAndPassStr = null;

            bool equal = false;
            int pos = 0;
            bool successful = IteratePassword(user, b =>
            {
                equal = pass[pos++] == b;
                return equal;
            });

            return successful && equal;
        }

        protected bool ValidateUser(DigestAuthHeaderValue headerValue)
        {
            return headerValue.ValidateAgainst(this);
        }

        #endregion

        #region Internal Methods

        internal bool HashPasswordToDigest(string user, MessageDigest digest)
        {
            return IteratePassword(user, b =>
            {
                digest.Update(b);
                return true;
            });
        }

        #endregion

        #region Private Methods

        private bool IteratePassword(string user, Func<byte, bool> func)
        {
            SecureString securedPass;
            if (!_passwordMap.TryGetValue(user, out securedPass)) {
                return false;
            }

            var marshaled = Marshal.SecureStringToGlobalAllocAnsi(securedPass);
            byte next;
            int offset = 0;
            bool keepGoing = true;
            while(keepGoing && (next = Marshal.ReadByte(marshaled, offset++)) != 0) {
                keepGoing = func(next);
            }

            Marshal.ZeroFreeGlobalAllocAnsi(marshaled);
            return true;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) {
                return;
            }

            DisposeInternal();
            _disposed = true;
        }

        #endregion
    }
}

