//
// Body.cs
//
// Author:
//     Zachary Gramana  <zack@xamarin.com>
//
// Copyright (c) 2014 Xamarin Inc
// Copyright (c) 2014 .NET Foundation
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
//
// Copyright (c) 2014 Couchbase, Inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file
// except in compliance with the License. You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software distributed under the
// License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND,
// either express or implied. See the License for the specific language governing permissions
// and limitations under the License.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Sharpen;

namespace Couchbase.Lite
{
    /// <summary>
    /// A request/response/document body, stored as either JSON, IDictionary&gt;String,Object&gt;, or IList&gt;object&gt;
    /// </summary>
    public sealed class Body
    {

        #region Variables

        private IEnumerable<byte> _json;
        private object _jsonObject;

        #endregion

        #region Constructors

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="json">An enumerable collection of bytes storing JSON</param>
        public Body(IEnumerable<Byte> json)
        {
            _json = json;
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="properties">An IDictionary containing the properties and objects to serialize</param>
        public Body(IDictionary<string, object> properties)
        {
            _jsonObject = properties;
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="array">An IList containing a list of objects to serialize</param>
        public Body(IList<object> array)
        {
            _jsonObject = array;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Determines whether this instance is valid JSON.
        /// </summary>
        /// <returns><c>true</c> if this instance is valid JSON; otherwise, <c>false</c>.</returns>
        public bool IsValidJSON()
        {
            if (_jsonObject == null) {
                if (_json == null) {
                    throw new InvalidOperationException("Both object and json are null for this body: " + this);
                }

                try {
                    _jsonObject = Manager.GetObjectMapper().ReadValue<object>(_json);
                } catch (IOException) {
                }
            }
            return _jsonObject != null;
        }

        /// <summary>
        /// Returns a serialized JSON byte enumerable object containing the properties
        /// of this object.
        /// </summary>
        /// <returns>JSON bytes</returns>
        public IEnumerable<byte> AsJson()
        {
            if (_json == null) {
                LazyLoadJsonFromObject();
            }

            return _json;
        }

        /// <summary>
        /// Returns a serialized JSON byte enumerable object containing the properties
        /// of this object in human readable form.
        /// </summary>
        /// <returns>JSON bytes</returns>
        public IEnumerable<Byte> AsPrettyJson()
        {
            object properties = AsObject();
            if (properties != null) {
                ObjectWriter writer = Manager.GetObjectMapper().WriterWithDefaultPrettyPrinter();

                try {
                    _json = writer.WriteValueAsBytes(properties);
                } catch (IOException e) {
                    throw new InvalidDataException("The array or dictionary stored is corrupt", e);
                }
            }

            return AsJson();
        }

        /// <summary>
        /// Returns a serialized JSON string containing the properties
        /// of this object
        /// </summary>
        /// <returns>JSON string</returns>
        public string AsJSONString()
        {
            return Encoding.UTF8.GetString(AsJson().ToArray());
        }

        /// <summary>
        /// Gets the deserialized object containing the properties of the JSON
        /// </summary>
        /// <returns>The deserialized object (either IDictionary or IList)</returns>
        public object AsObject()
        {
            if (_jsonObject == null) {
                LazyLoadObjectFromJson();
            }

            return _jsonObject;
        }

        /// <summary>
        /// Gets the properties from this object
        /// </summary>
        /// <returns>The properties contained in the object</returns>
        public IDictionary<string, object> GetProperties()
        {
            var currentObj = AsObject();
            return currentObj.AsDictionary<string, object>();
        }

        /// <summary>
        /// Determines whether this instance has value the specified key.
        /// </summary>
        /// <returns><c>true</c> if this instance has value for the specified key; otherwise, <c>false</c>.</returns>
        /// <param name="key">The key to check</param>
        public bool HasValueForKey(string key)
        {
            return GetProperties().ContainsKey(key);
        }

        /// <summary>
        /// Gets the property for the given key
        /// </summary>
        /// <returns>The property for the given key</returns>
        /// <param name="key">Key.</param>
        public object GetPropertyForKey(string key)
        {
            IDictionary<string, object> theProperties = GetProperties();
            if (theProperties == null) {
                return null;
            }

            return theProperties.Get(key);
        }

        /// <summary>
        /// Gets the cast property for the given key, or uses the default value if not found
        /// </summary>
        /// <returns>The property for key, cast to T</returns>
        /// <param name="key">The key to search for</param>
        /// <param name="defaultVal">The value to use if the key is not found</param>
        /// <typeparam name="T">The type to cast to</typeparam>
        public T GetPropertyForKey<T>(string key, T defaultVal = default(T))
        {
            IDictionary<string, object> theProperties = GetProperties();
            if (theProperties == null) {
                return defaultVal;
            }

            return theProperties.GetCast<T>(key, defaultVal);
        }

        /// <summary>
        /// Tries the get property for key and cast it to T
        /// </summary>
        /// <returns><c>true</c>, if the property was found and cast, <c>false</c> otherwise.</returns>
        /// <param name="key">The key to search for</param>
        /// <param name="val">The cast value, if successful</param>
        /// <typeparam name="T">The type to cast to</typeparam>
        public bool TryGetPropertyForKey<T>(string key, out T val)
        {
            val = default(T);
            IDictionary<string, object> theProperties = GetProperties();
            if (theProperties == null) {
                return false;
            }

            object valueObj;
            if (!theProperties.TryGetValue(key, out valueObj)) {
                return false;
            }

            return ExtensionMethods.TryCast<T>(valueObj, out val);
        }

        #endregion

        #region Private Methods

        // Attempt to serialize _jsonObject
        private void LazyLoadJsonFromObject()
        {
            if (_jsonObject == null) {
                throw new InvalidOperationException("Both json and object are null for this body: " + this);
            }

            try {
                _json = Manager.GetObjectMapper().WriteValueAsBytes(_jsonObject);
            } catch (IOException e) {
                throw new InvalidDataException("The array or dictionary stored is corrupt", e);
            }
        }
          
        // Attempt to deserialize /json
        private void LazyLoadObjectFromJson()
        {
            if (_json == null) {
                throw new InvalidOperationException("Both object and json are null for this body: "
                + this);
            }

            try {
                _jsonObject = Manager.GetObjectMapper().ReadValue<IDictionary<string,object>>(_json);
            } catch (IOException e) {
                throw new InvalidDataException("The JSON stored is corrupt", e);
            }
        }

        #endregion
    }
}
