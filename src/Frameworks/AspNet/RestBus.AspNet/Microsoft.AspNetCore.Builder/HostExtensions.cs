﻿using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RestBus.AspNet;
using RestBus.AspNet.Server;
using RestBus.Common;
using System;
using System.Linq;
using System.Diagnostics;

namespace Microsoft.AspNetCore.Builder
{

    public static class HostExtensions
    {

        //TODO: See if it's possible to prevent middleware from being added after RunRestBusHost() is called, subsequent calls to RunRestBusHost must succeed.

        /// <summary>
        /// Starts running a new RestBus host.
        /// </summary>
        /// <param name="app">The Application builder</param>
        /// <param name="subscriber">The RestBus subscriber</param>
        /// <remarks>The RestBus host will not be started if the application is running a RestBus server.</remarks>
        public static void RunRestBusHost(this IApplicationBuilder app, IRestBusSubscriber subscriber)
        {
            RunRestBusHost(app, subscriber, false);
        }

        /// <summary>
        /// Starts running a new RestBus host.
        /// </summary>
        /// <param name="app">The Application builder</param>
        /// <param name="subscriber">The RestBus subscriber</param>
        /// <param name="skipRestBusServerCheck">Set to true to run the host even if the application server is the RestBus.AspNet server</param>
        public static void RunRestBusHost(this IApplicationBuilder app, IRestBusSubscriber subscriber, bool skipRestBusServerCheck)
        {
            if (app == null) throw new ArgumentNullException("app");
            if (subscriber == null) throw new ArgumentNullException("subscriber");

            if (!skipRestBusServerCheck && RestBus.AspNet.Server.Server.InstanceCount > 0)
            {
                //The application is running RestBusServer, so exit
                return;
            }

            var appFunc = app.Build();

            var _loggerFactory = app.ApplicationServices.GetRequiredService<ILoggerFactory>();
            var diagnosticSource = app.ApplicationServices.GetRequiredService<DiagnosticSource>();
            var httpContextFactory = app.ApplicationServices.GetRequiredService<IHttpContextFactory>();

            //TODO: Work on counting instances (all hosts + server)  and adding the count to the logger name e.g RestBus.AspNet (2), consider including the typename as well.
            var application = new HostingApplication(appFunc, _loggerFactory.CreateLogger(typeof(RestBus.AspNet.Server.Server).FullName), diagnosticSource, httpContextFactory);
            var server = app.ApplicationServices.GetRequiredService<RestBus.AspNet.Server.Server>();

            server.Start(application, subscriber);
        }

    }

}