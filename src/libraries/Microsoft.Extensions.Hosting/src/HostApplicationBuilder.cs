// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting
{
    public class HostApplicationBuilder
    {
        private readonly HostBuilderContext _hostBuilderContext;
        private readonly DefaultServiceProviderFactory _defaultServiceProviderFactory;

        private IServiceProvider _appServices;
        private bool _hostBuilt;

        /// <summary>
        /// Initializes a new instance of the <see cref="HostApplicationBuilder"/>.
        /// </summary>
        /// <param name="options">Options controlling initial configuration and whether default settings should beOptions controlling initial configuration and whether default settings should be usedd.</param>
        public HostApplicationBuilder(HostApplicationOptions options)
        {
            Configuration = options.InitialConfiguration ?? new ConfigurationManager();

            if (!options.DisableDefaults)
            {
                HostingHostBuilderExtensions.ApplyDefaultHostConfiguration(Configuration, options.Args);
            }

            // HostApplicationOptions override all other config sources.
            List<KeyValuePair<string, string>> optionList = null;
            if (options.ApplicationName is not null)
            {
                optionList ??= new();
                optionList.Add(new KeyValuePair<string, string>(HostDefaults.ApplicationKey, options.ApplicationName));
            }
            if (options.EnvironmentName is not null)
            {
                optionList ??= new();
                optionList.Add(new KeyValuePair<string, string>(HostDefaults.EnvironmentKey, options.EnvironmentName));
            }
            if (options.ContentRootPath is not null)
            {
                optionList ??= new();
                optionList.Add(new KeyValuePair<string, string>(HostDefaults.ContentRootKey, options.ContentRootPath));
            }
            if (optionList is not null)
            {
                Configuration.AddInMemoryCollection(optionList);
            }

            var (hostingEnvironment, physicalFileProvider) = HostBuilder.CreateHostingEnvironment(Configuration);

            Configuration.SetFileProvider(physicalFileProvider);

            _hostBuilderContext = new HostBuilderContext(new Dictionary<object, object>())
            {
                HostingEnvironment = hostingEnvironment,
                Configuration = Configuration,
            };

            Environment = hostingEnvironment;

            Services = HostBuilder.CreateServiceCollection(
                _hostBuilderContext,
                hostingEnvironment,
                physicalFileProvider,
                Configuration,
                () => _appServices);

            Logging = new LoggingBuilder(Services);

            if (options.DisableDefaults)
            {
                _defaultServiceProviderFactory = new DefaultServiceProviderFactory();
            }
            else
            {
                HostingHostBuilderExtensions.ApplyDefaultAppConfiguration(_hostBuilderContext, Configuration, options.Args);
                HostingHostBuilderExtensions.AddDefaultServices(_hostBuilderContext, Services);
                _defaultServiceProviderFactory = HostingHostBuilderExtensions.CreateDefaultServiceProviderFactory(_hostBuilderContext);
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
        /// A collection of logging providers for the application to compose. This is useful for adding new logging providers.
        /// </summary>
        public ILoggingBuilder Logging { get; }

        /// <summary>
        /// Build the host. This can only be called once.
        /// </summary>
        /// <returns>An initialized <see cref="IHost"/>.</returns>
        public IHost Build()
        {
            return Build(_defaultServiceProviderFactory);
        }

        /// <summary>
        /// Build the host. This can only be called once.
        /// </summary>
        /// <returns>An initialized <see cref="IHost"/>.</returns>
        public IHost Build<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> serviceProviderFactory)
        {
            if (serviceProviderFactory is null)
            {
                throw new ArgumentNullException(nameof(serviceProviderFactory));
            }
            if (_hostBuilt)
            {
                throw new InvalidOperationException(SR.BuildCalled);
            }

            _hostBuilt = true;

            var hostBuilderAdapter = new HostBuilderAdapter(_hostBuilderContext, Configuration, Services,
                new ServiceFactoryAdapter<TContainerBuilder>(serviceProviderFactory));

            using DiagnosticListener diagnosticListener = HostBuilder.LogHostBuilding(hostBuilderAdapter);

            _appServices = hostBuilderAdapter.CreateServiceProvider();
            var host = _appServices.GetRequiredService<IHost>();

            HostBuilder.LogHostBuilt(diagnosticListener, host);

            return host;
        }

        private class HostBuilderAdapter : IHostBuilder
        {
            private readonly HostBuilderContext _hostBuilderContext;
            private readonly ConfigurationManager _configuration;
            private readonly IServiceCollection _services;

            private readonly List<Action<IConfigurationBuilder>> _configureHostConfigActions = new();
            private readonly List<Action<HostBuilderContext, IConfigurationBuilder>> _configureAppConfigActions = new();
            private readonly List<IConfigureContainerAdapter> _configureContainerActions = new();
            private readonly List<Action<HostBuilderContext, IServiceCollection>> _configureServicesActions = new();

            private IServiceFactoryAdapter _serviceProviderFactory;

            public HostBuilderAdapter(HostBuilderContext hostBuilderContext, ConfigurationManager configuration, IServiceCollection services, IServiceFactoryAdapter serviceProviderFactory)
            {
                _hostBuilderContext = hostBuilderContext;
                _configuration = configuration;
                _services = services;
                _serviceProviderFactory = serviceProviderFactory;
            }

            public IServiceProvider CreateServiceProvider()
            {
                foreach (Action<IConfigurationBuilder> configureHostAction in _configureHostConfigActions)
                {
                    configureHostAction(_configuration);
                }
                foreach (Action<HostBuilderContext, IConfigurationBuilder> configureAppAction in _configureAppConfigActions)
                {
                    configureAppAction(_hostBuilderContext, _configuration);
                }

                return HostBuilder.CreateServiceProvider(
                    _hostBuilderContext,
                    _services,
                    _serviceProviderFactory,
                    _configureServicesActions,
                    _configureContainerActions);
            }

            public IDictionary<object, object> Properties => _hostBuilderContext.Properties;

            public IHost Build() => throw new NotImplementedException();

            public IHostBuilder ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate)
            {
                // TODO: Provide compatibility implementation similar to https://github.com/dotnet/aspnetcore/blob/15fa3ad10859abcc54e3ad5557dc928f6c94994d/src/DefaultBuilder/src/ConfigureHostBuilder.cs
                // that prevents modifications to HostDefaults.ApplicationKey, HostDefaults.ContentRootKey and HostDefaults.EnvironmentKey in ConfigureHostConfiguration since it's too late.
                _configureHostConfigActions.Add(configureDelegate ?? throw new ArgumentNullException(nameof(configureDelegate)));
                return this;
            }

            public IHostBuilder ConfigureAppConfiguration(Action<HostBuilderContext, IConfigurationBuilder> configureDelegate)
            {
                _configureAppConfigActions.Add(configureDelegate ?? throw new ArgumentNullException(nameof(configureDelegate)));
                return this;
            }

            public IHostBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
            {
                _configureServicesActions.Add(configureDelegate ?? throw new ArgumentNullException(nameof(configureDelegate)));
                return this;
            }

            public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory)
            {
                _serviceProviderFactory = new ServiceFactoryAdapter<TContainerBuilder>(factory ?? throw new ArgumentNullException(nameof(factory)));
                return this;

            }

            public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(Func<HostBuilderContext, IServiceProviderFactory<TContainerBuilder>> factory)
            {
                _serviceProviderFactory = new ServiceFactoryAdapter<TContainerBuilder>(() => _hostBuilderContext, factory ?? throw new ArgumentNullException(nameof(factory)));
                return this;
            }

            public IHostBuilder ConfigureContainer<TContainerBuilder>(Action<HostBuilderContext, TContainerBuilder> configureDelegate)
            {
                _configureContainerActions.Add(new ConfigureContainerAdapter<TContainerBuilder>(configureDelegate
                    ?? throw new ArgumentNullException(nameof(configureDelegate))));

                return this;
            }
        }

        private sealed class LoggingBuilder : ILoggingBuilder
        {
            public LoggingBuilder(IServiceCollection services)
            {
                Services = services;
            }

            public IServiceCollection Services { get; }
        }
    }
}
