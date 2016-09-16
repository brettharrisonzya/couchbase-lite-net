//
// ServiceBrowser.cs
//
// Authors:
//    Aaron Bockover  <abockover@novell.com>
//
// Copyright (C) 2006-2007 Novell, Inc (http://www.novell.com)
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
//
//  Modifications by Jim Borden <jim.borden@couchbase.com>
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
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;

using Couchbase.Lite.Util;

#if __IOS__
using AOT = ObjCRuntime;
#endif

namespace Mono.Zeroconf.Providers.Bonjour
{
    public class ServiceBrowseEventArgs : Mono.Zeroconf.ServiceBrowseEventArgs
    {
        private bool more_coming;
        
        public ServiceBrowseEventArgs(BrowseService service, bool moreComing) : base(service)
        {
            this.more_coming = moreComing;
        }
        
        public bool MoreComing {
            get { return more_coming; }
        }
    }
    
    public class ServiceBrowser : IServiceBrowser, IDisposable
    {
        private const string TAG = "ServiceBrowser";

        #if __UNITY_ANDROID__
        private static ManualResetEventSlim _primedEvent = new ManualResetEventSlim();
        #endif

        private uint interface_index;
        private AddressProtocol address_protocol;
        private string regtype;
        private string domain;
        
        private ServiceRef sd_ref = ServiceRef.Zero;
        private Dictionary<string, IResolvableService> service_table = new Dictionary<string, IResolvableService> ();
        
        private Native.DNSServiceBrowseReply browse_reply_handler;
        private GCHandle _self;
        
        private Thread thread;
        
        public event ServiceBrowseEventHandler ServiceAdded
        {
            add { _serviceAdded = (ServiceBrowseEventHandler)Delegate.Combine(_serviceAdded, value); }
            remove { _serviceAdded = (ServiceBrowseEventHandler)Delegate.Remove(_serviceAdded, value); }
        }
        private event ServiceBrowseEventHandler _serviceAdded;

        public event ServiceBrowseEventHandler ServiceRemoved
        {
            add { _serviceRemoved = (ServiceBrowseEventHandler)Delegate.Combine(_serviceRemoved, value); }
            remove { _serviceRemoved = (ServiceBrowseEventHandler)Delegate.Remove(_serviceRemoved, value); }
        }
        private event ServiceBrowseEventHandler _serviceRemoved;

        #if __ANDROID__
        /// <summary>
        /// This is needed to start the /system/bin/mdnsd service on Android
        /// (can't find another way to start it)
        /// </summary>
        static ServiceBrowser() {
            global::Android.App.Application.Context.GetSystemService("servicediscovery");
        }
        #elif __UNITY_ANDROID__
        static ServiceBrowser() {
            Couchbase.Lite.Unity.UnityMainThreadScheduler.TaskFactory.StartNew(() =>
            {
                UnityEngine.AndroidJavaClass c = new UnityEngine.AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var context = c.GetStatic<UnityEngine.AndroidJavaObject>("currentActivity");
                if (context == null) {
                    c.Dispose();
                    throw new Exception("Failed to get context");
                }

                var arg = new UnityEngine.AndroidJavaObject("java.lang.String", "servicediscovery");
                context.Call<UnityEngine.AndroidJavaObject>("getSystemService", arg);

                context.Dispose();
                arg.Dispose();
                c.Dispose();
                _primedEvent.Set();
            });
        }
        #endif
        
        public ServiceBrowser()
        {
            #if __UNITY_ANDROID__
            if (!_primedEvent.Wait(10000)) {
                throw new TimeoutException("Timeout waiting for mDNS daemon to start");
            }
            #endif

            browse_reply_handler = new Native.DNSServiceBrowseReply(OnBrowseReply);
        }
        
        public void Browse (uint interfaceIndex, AddressProtocol addressProtocol, string regtype, string domain)
        {
            Configure(interfaceIndex, addressProtocol, regtype, domain);
            StartAsync();
        }

        public void Configure(uint interfaceIndex, AddressProtocol addressProtocol, string regtype, string domain)
        {
            this.interface_index = interfaceIndex;
            this.address_protocol = addressProtocol;
            this.regtype = regtype;
            this.domain = domain;
            
            if(regtype == null) {
                Log.To.Discovery.E(TAG, "regtype cannot be null in Configure, throwing...");
                throw new ArgumentNullException("regtype");
            }
        }
        
        private void Start(bool @async)
        {
            if(thread != null) {
                Log.To.Discovery.E(TAG, "Attempt to call start on an already started object, throwing...");
                throw new InvalidOperationException("ServiceBrowser is already started");
            }
            
            if(@async) {
                thread = new Thread(new ThreadStart(ThreadedStart));
                thread.IsBackground = true;
                thread.Start();
            } else {
                ProcessStart();
            }
        }
        
        public void Start()
        {
            Start(false);
        }
        
        public void StartAsync()
        {
            Start(true);
        }
        
        private void ThreadedStart()
        {
            try {
                ProcessStart();
            } catch(ThreadAbortException) {
                Thread.ResetAbort();
                Log.To.Discovery.I(TAG, "Finished");
            }
            
            thread = null;
        }

        private void ProcessStart()
        {
            _self = GCHandle.Alloc(this);
            ServiceError error = ServiceError.NoError;
            try {
                Log.To.Discovery.V(TAG, "{0} Preparing to enter DNSServiceBrowse");
                error = Native.DNSServiceBrowse(out sd_ref, ServiceFlags.Default,
                    interface_index, regtype, domain, browse_reply_handler, GCHandle.ToIntPtr(_self));
            } catch (DllNotFoundException) {
                Log.To.Discovery.E(TAG, "Unable to find required DLL file:  dnssd.dll\n" +
                    "(Windows -> Is Bonjour installed?)\n" +
                    "(Linux -> Are the network support files in place?)\n" +
                    "(Others -> Is a dll.config file included?)");
                error = ServiceError.BadState;
            }

            if(error != ServiceError.NoError) {
                if ((int)error == -65563) {
                    Log.To.Discovery.E(TAG, "mDNS daemon not started, throwing...");
                } else {
                    Log.To.Discovery.E(TAG, "Got error in DNSServiceBrowse ({0}), throwing...", error);
                }

                throw new ServiceErrorException(error);
            }

            sd_ref.Process();
        }
        
        public void Stop()
        {
            _self.Free();
            if(thread != null) {
                thread.Abort();
                thread = null;
            }

            if(sd_ref != ServiceRef.Zero) {
                sd_ref.Deallocate();
                sd_ref = ServiceRef.Zero;
            }
        }
        
        public void Dispose()
        {
            Stop();
        }
        
        public IEnumerator<IResolvableService> GetEnumerator ()
        {
            lock (service_table) {
                foreach (IResolvableService service in service_table.Values) {
                    yield return service;
                }
            }
        }
        
        IEnumerator IEnumerable.GetEnumerator ()
        {
            return GetEnumerator ();
        }

        #if __IOS__ || __UNITY_APPLE__
        [AOT.MonoPInvokeCallback(typeof(Native.DNSServiceBrowseReply))]
        #endif
        private static void OnBrowseReply(ServiceRef sdRef, ServiceFlags flags, uint interfaceIndex, ServiceError errorCode, 
            string serviceName, string regtype, string replyDomain, IntPtr context)
        {
            var handle = GCHandle.FromIntPtr(context);
            var serviceBrowser = handle.Target as ServiceBrowser;
            BrowseService service = new BrowseService();
            service.Flags = flags;
            service.Name = serviceName;
            service.RegType = regtype;
            service.ReplyDomain = replyDomain;
            service.InterfaceIndex = interfaceIndex;
            service.AddressProtocol = serviceBrowser.address_protocol;

            Log.To.Discovery.V(TAG, "{0} (0x{1}) entered OnBrowseReply (found={2} flags={3})", 
                serviceBrowser, sdRef.Raw.ToString("X"), service, flags);
            
            ServiceBrowseEventArgs args = new ServiceBrowseEventArgs(
                service, (flags & ServiceFlags.MoreComing) != 0);
            
            if((flags & ServiceFlags.Add) != 0) {
                lock (serviceBrowser.service_table) {
                    if (serviceBrowser.service_table.ContainsKey (serviceName)) {
                        serviceBrowser.service_table[serviceName] = service;
                    } else {
                        serviceBrowser.service_table.Add (serviceName, service);
                    }
                }
                
                ServiceBrowseEventHandler handler = serviceBrowser._serviceAdded;
                if(handler != null) {
                    handler(serviceBrowser, args);
                }
            } else {
                lock (serviceBrowser.service_table) {
                    if (serviceBrowser.service_table.ContainsKey (serviceName)) {
                        serviceBrowser.service_table.Remove (serviceName);
                    }
                }
                
                ServiceBrowseEventHandler handler = serviceBrowser._serviceRemoved;
                if(handler != null) {
                    handler(serviceBrowser, args);
                }

                service.Dispose();
            }
        }

        public override string ToString()
        {
            return string.Format("ServiceBrowser[Type={0} Domain={1}]", regtype, domain);
        }
    }
}
