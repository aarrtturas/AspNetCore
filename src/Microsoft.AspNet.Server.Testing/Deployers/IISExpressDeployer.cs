﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Framework.Logging;
using Microsoft.Framework.Runtime;
using Microsoft.Framework.Runtime.Infrastructure;

namespace Microsoft.AspNet.Server.Testing
{
    /// <summary>
    /// Deployment helper for IISExpress.
    /// </summary>
    public class IISExpressDeployer : ApplicationDeployer
    {
        private Process _hostProcess;

        public IISExpressDeployer(DeploymentParameters deploymentParameters, ILogger logger)
            : base(deploymentParameters, logger)
        {
        }

        public override DeploymentResult Deploy()
        {
            // Start timer
            StartTimer();

            DeploymentParameters.DnxRuntime = PopulateChosenRuntimeInformation();

            if (DeploymentParameters.PublishApplicationBeforeDeployment)
            {
                DnuPublish();
            }

            // Launch the host process.
            var hostExitToken = StartIISExpress();

            return new DeploymentResult
            {
                WebRootLocation = DeploymentParameters.ApplicationPath,
                DeploymentParameters = DeploymentParameters,
                // Right now this works only for urls like http://localhost:5001/. Does not work for http://localhost:5001/subpath.
                ApplicationBaseUri = DeploymentParameters.ApplicationBaseUriHint,
                HostShutdownToken = hostExitToken
            };
        }

        private CancellationToken StartIISExpress()
        {
            if (!string.IsNullOrWhiteSpace(DeploymentParameters.ApplicationHostConfigTemplateContent))
            {
                // Pass on the applicationhost.config to iis express. With this don't need to pass in the /path /port switches as they are in the applicationHost.config
                // We take a copy of the original specified applicationHost.Config to prevent modifying the one in the repo.

                DeploymentParameters.ApplicationHostConfigTemplateContent =
                    DeploymentParameters.ApplicationHostConfigTemplateContent
                    .Replace("[ApplicationPhysicalPath]", Path.Combine(DeploymentParameters.ApplicationPath, "wwwroot"))
                    .Replace("[PORT]", new Uri(DeploymentParameters.ApplicationBaseUriHint).Port.ToString());

                DeploymentParameters.ApplicationHostConfigLocation = Path.GetTempFileName();

                File.WriteAllText(DeploymentParameters.ApplicationHostConfigLocation,
                    DeploymentParameters.ApplicationHostConfigTemplateContent.Replace("[ApplicationPhysicalPath]", DeploymentParameters.ApplicationPath));
            }

            if (!DeploymentParameters.PublishApplicationBeforeDeployment)
            {
                CopyAspNetLoader();
            }

            var webroot = DeploymentParameters.ApplicationPath;
            if (!webroot.EndsWith("wwwroot"))
            {
                webroot = Path.Combine(webroot, "wwwroot");
            }

            var parameters = string.IsNullOrWhiteSpace(DeploymentParameters.ApplicationHostConfigLocation) ?
                            string.Format("/port:{0} /path:\"{1}\" /trace:error", new Uri(DeploymentParameters.ApplicationBaseUriHint).Port, webroot) :
                            string.Format("/site:{0} /config:{1} /trace:error", DeploymentParameters.SiteName, DeploymentParameters.ApplicationHostConfigLocation);

            var iisExpressPath = GetIISExpressPath();

            Logger.LogInformation("Executing command : {iisExpress} {args}", iisExpressPath, parameters);

            var startInfo = new ProcessStartInfo
            {
                FileName = iisExpressPath,
                Arguments = parameters,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            AddEnvironmentVariablesToProcess(startInfo);

            // IIS express figures out the DNX from %PATH%.
#if DNX451
            SetEnvironmentVariable(startInfo.EnvironmentVariables, "PATH", ChosenRuntimePath + ";" + startInfo.EnvironmentVariables["PATH"]);
            SetEnvironmentVariable(startInfo.EnvironmentVariables, "DNX_APPBASE", DeploymentParameters.ApplicationPath);
#elif DNXCORE50
            SetEnvironmentVariable(startInfo.Environment, "PATH", ChosenRuntimePath + ";" + startInfo.Environment["PATH"]);
            SetEnvironmentVariable(startInfo.Environment, "DNX_APPBASE", DeploymentParameters.ApplicationPath);
#endif

            _hostProcess = Process.Start(startInfo);
            _hostProcess.EnableRaisingEvents = true;
            var hostExitTokenSource = new CancellationTokenSource();
            _hostProcess.Exited += (sender, e) =>
            {
                TriggerHostShutdown(hostExitTokenSource);
            };

            if (_hostProcess.HasExited)
            {
                Logger.LogError("Host process {processName} exited with code {exitCode} or failed to start.", startInfo.FileName, _hostProcess.ExitCode);
                throw new Exception("Failed to start host");
            }

            Logger.LogInformation("Started iisexpress. Process Id : {processId}", _hostProcess.Id);
            return hostExitTokenSource.Token;
        }

        private void CopyAspNetLoader()
        {
            var libraryManager = (ILibraryManager)CallContextServiceLocator.Locator.ServiceProvider.GetService(typeof(ILibraryManager));
            var interopLibrary = libraryManager.GetLibraryInformation("Microsoft.AspNet.Loader.IIS.Interop");

            if (interopLibrary == null)
            {
                throw new Exception(
                    string.Format("Include Microsoft.AspNet.Server.IIS package in your project.json to deploy in {0}.",
                        ServerType.IISExpress));
            }

            var aspNetLoaderSrcPath = Path.Combine(interopLibrary.Path, "tools", "AspNet.Loader.dll");
            var aspNetLoaderDestPath = Path.Combine(DeploymentParameters.ApplicationPath, "wwwroot", "bin", "AspNet.Loader.dll");

            // Create bin directory if it does not exist.
            Directory.CreateDirectory(new DirectoryInfo(aspNetLoaderDestPath).Parent.FullName);

            if (!File.Exists(aspNetLoaderDestPath))
            {
                try
                {
                    File.Copy(aspNetLoaderSrcPath, aspNetLoaderDestPath);
                }
                catch (IOException)
                {
                    // Ignore file already exists exception. Sometimes multiple tests might try 
                    // doing the same and one of them wins.
                }
            }
        }

        private string GetIISExpressPath()
        {
            // Get path to program files
            var iisExpressPath = Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)"), "IIS Express", "iisexpress.exe");

            // Get path to 64 bit of IIS Express
            if (DeploymentParameters.RuntimeArchitecture == RuntimeArchitecture.x64)
            {
                iisExpressPath = Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles"), "IIS Express", "iisexpress.exe");

                // If process is 32 bit, the path points to x86. Replace path to point to x64
                iisExpressPath = IntPtr.Size == 8 ? iisExpressPath : iisExpressPath.Replace(" (x86)", "");
            }

            if (!File.Exists(iisExpressPath))
            {
                throw new Exception("Unable to find IISExpress on the machine");
            }

            return iisExpressPath;
        }

        public override void Dispose()
        {
            ShutDownIfAnyHostProcess(_hostProcess);

            if (!string.IsNullOrWhiteSpace(DeploymentParameters.ApplicationHostConfigLocation)
                && File.Exists(DeploymentParameters.ApplicationHostConfigLocation))
            {
                // Delete the temp applicationHostConfig that we created.
                try
                {
                    File.Delete(DeploymentParameters.ApplicationHostConfigLocation);
                }
                catch (Exception exception)
                {
                    // Ignore delete failures - just write a log.
                    Logger.LogWarning("Failed to delete '{config}'. Exception : {exception}", DeploymentParameters.ApplicationHostConfigLocation, exception.Message);
                }
            }

            if (DeploymentParameters.PublishApplicationBeforeDeployment)
            {
                CleanPublishedOutput();
            }

            InvokeUserApplicationCleanup();

            StopTimer();
        }
    }
}