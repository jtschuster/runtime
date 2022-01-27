// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;

namespace Microsoft.Extensions.Hosting
{
    public class HostApplicationBuilder
    {
        private IServiceFactoryAdapter _serviceProviderFactory = new ServiceFactoryAdapter<IServiceCollection>(new DefaultServiceProviderFactory());

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
            throw new NotImplementedException();
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
