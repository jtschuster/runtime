// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;

namespace Microsoft.Extensions.Hosting
{
    public class HostApplicationBuilder
    {
        private IServiceFactoryAdapter _serviceProviderFactory = new ServiceFactoryAdapter<IServiceCollection>(new DefaultServiceProviderFactory());
        private List<IConfigureContainerAdapter> _configureContainerActions = new List<IConfigureContainerAdapter>();
        private bool _hostBuilt;

        public HostApplicationBuilder(HostApplicationOptions options)
        {
            var (hostingEnvironment, physicalFileProvider) = Hosting.HostBuilder.CreateHostingEnvironment(
                applicationName: options.ApplicationName,
                environmentName: options.EnvironmentName,
                contentRootPath: options.ContentRootPath);

            Configuration.SetFileProvider(physicalFileProvider);

            var hostBuilderContext = new HostBuilderContext(Properties)
            {
                HostingEnvironment = hostingEnvironment,
                Configuration = Configuration,
            };

            Environement = hostingEnvironment;

            Services = Hosting.HostBuilder.CreateServiceCollection(
                hostBuilderContext,
                hostingEnvironment,
                physicalFileProvider,
                Configuration);
        }

        /// <summary>
        /// A central location for sharing state between components during the host building process.
        /// </summary>
        public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

        public IHostEnvironment Environement { get; }

        public IServiceCollection Services { get; }

        public ConfigurationManager Configuration { get; } = new();

        public IHostBuilder HostBuilder { get; }

        public IHost Build()
        {
            if (_hostBuilt)
            {
                throw new InvalidOperationException(SR.BuildCalled);
            }
            _hostBuilt = true;

            // REVIEW: If we want to raise more events outside of these calls then we will need to
            // stash this in a field.
            using var diagnosticListener = new DiagnosticListener("Microsoft.Extensions.Hosting");
            const string hostBuildingEventName = "HostBuilding";
            const string hostBuiltEventName = "HostBuilt";

            if (diagnosticListener.IsEnabled() && diagnosticListener.IsEnabled(hostBuildingEventName))
            {
                Write(diagnosticListener, hostBuildingEventName, HostBuilder);
            }

            object containerBuilder = _serviceProviderFactory.CreateBuilder(Services);
            var serviceProvider = _serviceProviderFactory.CreateServiceProvider(containerBuilder);

            var appServices = Hosting.HostBuilder.CreateServiceProvider();

            var host = appServices.GetRequiredService<IHost>();
            if (diagnosticListener.IsEnabled() && diagnosticListener.IsEnabled(hostBuiltEventName))
            {
                Write(diagnosticListener, hostBuiltEventName, host);
            }

            return host;
        }

        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:UnrecognizedReflectionPattern",
            Justification = "The values being passed into Write are being consumed by the application already.")]
        private static void Write<T>(
            DiagnosticSource diagnosticSource,
            string name,
            T value)
        {
            diagnosticSource.Write(name, value);
        }

        private class HostBuilderAdapter : IHostBuilder
        {
            public IDictionary<object, object> Properties => throw new NotImplementedException();

            public IHost Build() => throw new NotImplementedException();
            public IHostBuilder ConfigureAppConfiguration(Action<HostBuilderContext, IConfigurationBuilder> configureDelegate) => throw new NotImplementedException();
            public IHostBuilder ConfigureContainer<TContainerBuilder>(Action<HostBuilderContext, TContainerBuilder> configureDelegate) => throw new NotImplementedException();
            public IHostBuilder ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate) => throw new NotImplementedException();
            public IHostBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate) => throw new NotImplementedException();
            public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory) => throw new NotImplementedException();
            public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(Func<HostBuilderContext, IServiceProviderFactory<TContainerBuilder>> factory) => throw new NotImplementedException();
        }
    }
}
