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
        private readonly List<IConfigureContainerAdapter> _configureContainerActions = new();
        private readonly HostBuilderContext _hostBuilderContext;

        private IServiceFactoryAdapter _serviceProviderFactory = new ServiceFactoryAdapter<IServiceCollection>(new DefaultServiceProviderFactory());
        private IServiceProvider _appServices;
        private bool _hostBuilt;

        public HostApplicationBuilder(HostApplicationOptions options)
        {
            Configuration = options.Configuration ?? new ConfigurationManager();

            var (hostingEnvironment, physicalFileProvider) = Hosting.HostBuilder.CreateHostingEnvironment(Configuration);

            Configuration.SetFileProvider(physicalFileProvider);

            _hostBuilderContext = new HostBuilderContext(HostBuilder.Properties)
            {
                HostingEnvironment = hostingEnvironment,
                Configuration = Configuration,
            };

            Environement = hostingEnvironment;

            Services = Hosting.HostBuilder.CreateServiceCollection(
                _hostBuilderContext,
                hostingEnvironment,
                physicalFileProvider,
                Configuration,
                () => _appServices);
        }

        public IHostEnvironment Environement { get; }

        public IServiceCollection Services { get; }

        public ConfigurationManager Configuration { get; }

        public IHostBuilder HostBuilder = new HostBuilderAdapter();

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
