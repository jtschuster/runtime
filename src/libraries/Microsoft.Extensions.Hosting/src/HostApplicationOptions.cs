// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Extensions.Hosting
{
    public class HostApplicationOptions
    {
        /// <summary>
        /// The environment name.
        /// </summary>
        public string EnvironmentName { get; set; }

        /// <summary>
        /// The application name.
        /// </summary>
        public string ApplicationName { get; set; }

        /// <summary>
        /// The content root path.
        /// </summary>
        public string ContentRootPath { get; set; }
    }
}
