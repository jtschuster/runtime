// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting
{
    public class HostApplicationOptions
    {
        /// <summary>
        /// If <see langword="true"/>, configures the <see cref="HostApplicationBuilder"/> instance with pre-configured defaults.
        /// This has a similar effect to calling <see cref="HostingHostBuilderExtensions.ConfigureDefaults(IHostBuilder, string[])"/>.
        /// </summary>
        /// <remarks>
        ///   The following defaults are applied to the <see cref="IHostBuilder"/>:
        ///     * set the <see cref="IHostEnvironment.ContentRootPath"/> to the result of <see cref="Directory.GetCurrentDirectory()"/>
        ///     * load <see cref="IConfiguration"/> from "DOTNET_" prefixed environment variables
        ///     * load <see cref="IConfiguration"/> from 'appsettings.json' and 'appsettings.[<see cref="IHostEnvironment.EnvironmentName"/>].json'
        ///     * load <see cref="IConfiguration"/> from User Secrets when <see cref="IHostEnvironment.EnvironmentName"/> is 'Development' using the entry assembly
        ///     * load <see cref="IConfiguration"/> from environment variables
        ///     * load <see cref="IConfiguration"/> from supplied command line args
        ///     * configure the <see cref="ILoggerFactory"/> to log to the console, debug, and event source output
        ///     * enables scope validation on the dependency injection container when <see cref="IHostEnvironment.EnvironmentName"/> is 'Development'
        /// </remarks>
        public bool ConfigureDefaults { get; set; }

        /// <summary>
        /// The command line arguments.
        /// </summary>
        public string[] Args { get; init; }

        /// <summary>
        /// Initial configuration sources to be added to the <see cref="HostApplicationBuilder.Configuration"/>. These sources can influence
        /// the <see cref="HostApplicationBuilder.Environement"/> through the use of <see cref="HostDefaults"/> keys. If <see cref="ConfigureDefaults"/>
        /// is <see langword="true"/>, these sources will be added after the default sources.
        /// </summary>
        public IList<IConfigurationSource> InitialialConfigurationSources { get; } = new List<IConfigurationSource>();
    }
}
