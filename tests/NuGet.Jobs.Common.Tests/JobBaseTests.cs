// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace NuGet.Jobs.Common.Tests
{
    public class JobBaseTests
    {
        private readonly TestJob _job;

        public JobBaseTests()
        {
            _job = new TestJob();
        }

        [Fact]
        public void SetJobTraceListener_AddsTraceListener()
        {
            var listener = new JobTraceListener();

            using (new TraceListenerCollectionResetter())
            {
                _job.SetJobTraceListener(listener);

                Assert.True(Trace.Listeners.Contains(listener), "A trace listener was not added.");
            }
        }

        [Fact]
        public void SetJobTraceListener_RemovesPreviousTraceListener()
        {
            var firstListener = new JobTraceListener();
            var secondListener = new JobTraceListener();

            using (new TraceListenerCollectionResetter())
            {
                _job.SetJobTraceListener(firstListener);
                _job.SetJobTraceListener(secondListener);

                Assert.False(Trace.Listeners.Contains(firstListener), "The previous trace listener was not removed.");
                Assert.True(Trace.Listeners.Contains(secondListener), "The new trace listener was not added.");
            }
        }

        private sealed class TraceListenerCollectionResetter : IDisposable
        {
            private readonly TraceListener[] _listeners;

            internal TraceListenerCollectionResetter()
            {
                _listeners = Trace.Listeners.Cast<TraceListener>().ToArray();
            }

            public void Dispose()
            {
                Trace.Listeners.Clear();
                Trace.Listeners.AddRange(_listeners);
            }
        }

        private sealed class TestJob : JobBase
        {
            public override bool Init(IDictionary<string, string> jobArgsDictionary)
            {
                throw new NotImplementedException();
            }

            public override Task<bool> Run()
            {
                throw new NotImplementedException();
            }
        }
    }
}