// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;

namespace Microsoft.Extensions.Hosting
{
    public class HostApplicationBuilder
    {
        private readonly List<IConfigureContainerAdapter> _configureContainerActions = new();
        private readonly HostBuilderContext _hostBuilderContext;

        private IServiceFactoryAdapter _serviceProviderFactory = new ServiceFactoryAdapter<IServiceCollection>(new DefaultServiceProviderFactory());
        private IServiceProvider _appServices;
        private bool _hostBuilt;

        internal HostApplicationBuilder(HostApplicationOptions options)
        {
            Configuration = options.Configuration ?? new ConfigurationManager();

            if (!options.DisableDefaults)
            {
                HostingHostBuilderExtensions.ApplyDefaultHostConfiguration(Configuration, options.Args);
            }

            var (hostingEnvironment, physicalFileProvider) = Hosting.HostBuilder.CreateHostingEnvironment(Configuration);

            Configuration.SetFileProvider(physicalFileProvider);

            _hostBuilderContext = new HostBuilderContext(HostBuilder.Properties)
            {
                HostingEnvironment = hostingEnvironment,
                Configuration = Configuration,
            };

            Environment = hostingEnvironment;

            Services = Hosting.HostBuilder.CreateServiceCollection(
                _hostBuilderContext,
                hostingEnvironment,
                physicalFileProvider,
                Configuration,
                () => _appServices);

            if (!options.DisableDefaults)
            {
                HostingHostBuilderExtensions.ApplyDefaultAppConfiguration(_hostBuilderContext, Configuration, options.Args);
                HostingHostBuilderExtensions.AddDefaultServices(_hostBuilderContext, Services);
                _serviceProviderFactory = new ServiceFactoryAdapter<IServiceCollection>(() => _hostBuilderContext, HostingHostBuilderExtensions.CreateDefaultServiceProvider);
            }
        }

        /// <summary>
        /// A collection of services for the application to compose. This is useful for adding user provided or framework provided services.
        /// </summary>
        public ConfigurationManager Configuration { get; }

        /// <summary>
        /// A collection of services for the application to compose. This is useful for adding user provided or framework provided services.
        /// </summary>
        public IServiceCollection Services { get; }

        /// <summary>
        /// Provides information about the hosting environment an application is running in.
        /// </summary>
        public IHostEnvironment Environment { get; }

        /// <summary>
        /// An <see cref="IHostBuilder"/> for configuring host specific properties, but not building.
        /// To build after configuration, call <see cref="Build"/>.
        /// </summary>
        public IHostBuilder HostBuilder { get; } = new HostBuilderAdapter();

        public IHost Build()
        {
            if (_hostBuilt)
            {
                throw new InvalidOperationException(SR.BuildCalled);
            }
            _hostBuilt = true;

            using DiagnosticListener diagnosticListener = Hosting.HostBuilder.LogHostBuilding(HostBuilder);

            _appServices = Hosting.HostBuilder.CreateServiceProvider(
                Services,
                _serviceProviderFactory,
                _configureContainerActions,
                _hostBuilderContext);

            var host = _appServices.GetRequiredService<IHost>();
            Hosting.HostBuilder.LogHostBuilt(diagnosticListener, host);

            return host;
        }

        // TODO: Provide compatibility implementation similar to https://github.com/dotnet/aspnetcore/blob/15fa3ad10859abcc54e3ad5557dc928f6c94994d/src/DefaultBuilder/src/ConfigureHostBuilder.cs
        // This will prevent modifications to HostDefaults.ApplicationKey, HostDefaults.ContentRootKey and HostDefaults.EnvironmentKey in ConfigureHostConfiguration since it's too late.
        private class HostBuilderAdapter : IHostBuilder
        {
            /// <summary>
            /// A central location for sharing state between components during the host building process.
            /// </summary>
            public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

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
