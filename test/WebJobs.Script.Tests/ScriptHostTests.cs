﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Host.Config;
using Microsoft.Azure.WebJobs.Script.Binding;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests
{
    public class ScriptHostTests : IClassFixture<ScriptHostTests.TestFixture>
    {
        private const string ID = "5a709861cab44e68bfed5d2c2fe7fc0c";
        private readonly TestFixture _fixture;
        private readonly ScriptSettingsManager _settingsManager;

        public ScriptHostTests(TestFixture fixture)
        {
            _fixture = fixture;
            _settingsManager = ScriptSettingsManager.Instance;
        }

        [Theory]
        [InlineData(@"C:\Functions\Scripts\Shared\Test.csx", "Shared")]
        [InlineData(@"C:\Functions\Scripts\Shared\Sub1\Sub2\Test.csx", "Shared")]
        [InlineData(@"C:\Functions\Scripts\Shared", "Shared")]
        public static void GetRelativeDirectory_ReturnsExpectedDirectoryName(string path, string expected)
        {
            Assert.Equal(expected, ScriptHost.GetRelativeDirectory(path, @"C:\Functions\Scripts"));
        }

        [Fact]
        public void ReadFunctionMetadata_Succeeds()
        {
            ScriptHostConfiguration config = new ScriptHostConfiguration.Builder()
                .WithRootScriptPath(Path.Combine(Environment.CurrentDirectory, @"..\..\..\..\sample"))
                .Build();

            var traceWriter = new TestTraceWriter(TraceLevel.Verbose);
            var functionErrors = new Dictionary<string, Collection<string>>();
            var metadata = ScriptHost.ReadFunctionMetadata(config, traceWriter, null, functionErrors);
            Assert.Equal(50, metadata.Count);
        }

        [Fact]
        public async Task OnDebugModeFileChanged_TriggeredWhenDebugFileUpdated()
        {
            ScriptHost host = _fixture.Host;
            string debugSentinelFilePath = Path.Combine(host.ScriptConfig.RootLogPath, "Host", ScriptConstants.DebugSentinelFileName);

            if (!File.Exists(debugSentinelFilePath))
            {
                File.WriteAllText(debugSentinelFilePath, string.Empty);
            }

            host.LastDebugNotify = DateTime.MinValue;
            Assert.False(host.InDebugMode);

            // verify that our file watcher for the debug sentinel file is configured
            // properly by touching the file and ensuring that our host goes into
            // debug mode
            File.SetLastWriteTimeUtc(debugSentinelFilePath, DateTime.UtcNow);

            await TestHelpers.Await(() =>
            {
                return host.InDebugMode;
            });

            Assert.True(host.InDebugMode);
        }

        [Fact]
        public void InDebugMode_ReturnsExpectedValue()
        {
            ScriptHost host = _fixture.Host;

            host.LastDebugNotify = DateTime.MinValue;
            Assert.False(host.InDebugMode);

            host.LastDebugNotify = DateTime.UtcNow - TimeSpan.FromSeconds(60 * ScriptHost.DebugModeTimeoutMinutes);
            Assert.False(host.InDebugMode);

            host.LastDebugNotify = DateTime.UtcNow - TimeSpan.FromSeconds(60 * (ScriptHost.DebugModeTimeoutMinutes - 1));
            Assert.True(host.InDebugMode);
        }

        [Fact]
        public void FileLoggingEnabled_ReturnsExpectedValue()
        {
            var environment = new Mock<IScriptHostEnvironment>();

            ScriptHostConfiguration config = new ScriptHostConfiguration.Builder()
                .WithHostId(ID)
                .WithFileLoggingMode(FileLoggingMode.DebugOnly)
                .Build();
            ScriptHost host = ScriptHost.Create(environment.Object, config);
            host.LastDebugNotify = DateTime.MinValue;
            Assert.Equal(FileLoggingMode.DebugOnly, host.ScriptConfig.FileLoggingMode);
            Assert.False(host.FileLoggingEnabled);
            host.NotifyDebug();
            Assert.True(host.FileLoggingEnabled);

            config = new ScriptHostConfiguration.Builder()
                .WithHostId(ID)
                .WithFileLoggingMode(FileLoggingMode.Never)
                .Build();
            host = ScriptHost.Create(environment.Object, config);
            Assert.Equal(FileLoggingMode.Never, host.ScriptConfig.FileLoggingMode);
            Assert.False(host.FileLoggingEnabled);

            config = new ScriptHostConfiguration.Builder()
                .WithHostId(ID)
                .WithFileLoggingMode(FileLoggingMode.Always)
                .Build();
            host = ScriptHost.Create(environment.Object, config);
            Assert.Equal(FileLoggingMode.Always, host.ScriptConfig.FileLoggingMode);
            Assert.True(host.FileLoggingEnabled);
            host.LastDebugNotify = DateTime.MinValue;
            Assert.True(host.FileLoggingEnabled);
        }

        [Fact]
        public void NotifyDebug_UpdatesDebugMarkerFileAndTimestamp()
        {
            ScriptHost host = _fixture.Host;

            string debugSentinelFileName = Path.Combine(host.ScriptConfig.RootLogPath, "Host", ScriptConstants.DebugSentinelFileName);
            File.Delete(debugSentinelFileName);
            host.LastDebugNotify = DateTime.MinValue;

            Assert.False(host.InDebugMode);

            DateTime lastDebugNotify = host.LastDebugNotify;
            host.NotifyDebug();
            Assert.True(host.InDebugMode);
            Assert.True(File.Exists(debugSentinelFileName));
            string text = File.ReadAllText(debugSentinelFileName);
            Assert.Equal("This is a system managed marker file used to control runtime debug mode behavior.", text);
            Assert.True(host.LastDebugNotify > lastDebugNotify);

            Thread.Sleep(500);

            DateTime lastModified = File.GetLastWriteTime(debugSentinelFileName);
            lastDebugNotify = host.LastDebugNotify;
            host.NotifyDebug();
            Assert.True(host.InDebugMode);
            Assert.True(File.Exists(debugSentinelFileName));
            Assert.True(File.GetLastWriteTime(debugSentinelFileName) > lastModified);
            Assert.True(host.LastDebugNotify > lastDebugNotify);
        }

        [Fact]
        public void NotifyDebug_HandlesExceptions()
        {
            ScriptHost host = _fixture.Host;
            string debugSentinelFileName = Path.Combine(host.ScriptConfig.RootLogPath, "Host", ScriptConstants.DebugSentinelFileName);

            try
            {
                host.NotifyDebug();
                Assert.True(host.InDebugMode);

                var attributes = File.GetAttributes(debugSentinelFileName);
                attributes |= FileAttributes.ReadOnly;
                File.SetAttributes(debugSentinelFileName, attributes);
                Assert.True(host.InDebugMode);

                host.NotifyDebug();
            }
            finally
            {
                File.SetAttributes(debugSentinelFileName, FileAttributes.Normal);
                File.Delete(debugSentinelFileName);
            }
        }

        [Fact]
        public void Version_ReturnsAssemblyVersion()
        {
            Assembly assembly = typeof(ScriptHost).Assembly;
            AssemblyFileVersionAttribute fileVersionAttribute = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>();

            string version = ScriptHost.Version;
            Assert.Equal(fileVersionAttribute.Version, version);
        }

        [Fact]
        public void GetAssemblyFileVersion_Unknown()
        {
            var asm = new AssemblyMock();
            var version = ScriptHost.GetAssemblyFileVersion(asm);

            Assert.Equal("Unknown", version);
        }

        [Fact]
        public void GetAssemblyFileVersion_ReturnsVersion()
        {
            var fileAttr = new AssemblyFileVersionAttribute("1.2.3.4");
            var asmMock = new Mock<AssemblyMock>();
            asmMock.Setup(a => a.GetCustomAttributes(typeof(AssemblyFileVersionAttribute), true))
               .Returns(new Attribute[] { fileAttr })
               .Verifiable();

            var version = ScriptHost.GetAssemblyFileVersion(asmMock.Object);

            Assert.Equal("1.2.3.4", version);
            asmMock.Verify();
        }

        [Theory]
        [InlineData("QUEUETriggER.py")]
        [InlineData("queueTrigger.py")]
        public void DeterminePrimaryScriptFile_MultipleFiles_SourceFileSpecified(string scriptFileName)
        {
            JObject functionConfig = new JObject()
            {
                { "scriptFile", scriptFileName }
            };

            var files = new Dictionary<string, MockFileData>
            {
                { @"c:\functions\queueTrigger.py", new MockFileData(string.Empty) },
                { @"c:\functions\helper.py", new MockFileData(string.Empty) },
                { @"c:\functions\test.txt", new MockFileData(string.Empty) }
            };

            var fileSystem = new MockFileSystem(files);

            string scriptFile = ScriptHost.DeterminePrimaryScriptFile(functionConfig, @"c:\functions", fileSystem);
            Assert.Equal(@"c:\functions\queueTrigger.py", scriptFile, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void DeterminePrimaryScriptFile_RelativeSourceFileSpecified()
        {
            JObject functionConfig = new JObject()
            {
                { "scriptFile", @"..\shared\queuetrigger.py" }
            };

            var files = new Dictionary<string, MockFileData>
            {
                { @"c:\shared\queueTrigger.py", new MockFileData(string.Empty) },
                { @"c:\functions\queueTrigger.py", new MockFileData(string.Empty) },
                { @"c:\functions\helper.py", new MockFileData(string.Empty) },
                { @"c:\functions\test.txt", new MockFileData(string.Empty) }
            };

            var fileSystem = new MockFileSystem(files);

            string scriptFile = ScriptHost.DeterminePrimaryScriptFile(functionConfig, @"c:\functions", fileSystem);
            Assert.Equal(@"c:\shared\queueTrigger.py", scriptFile, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void DeterminePrimaryScriptFile_MultipleFiles_ConfigTrumpsConvention()
        {
            JObject functionConfig = new JObject()
            {
                { "scriptFile", "queueTrigger.py" }
            };
            var files = new Dictionary<string, MockFileData>
            {
                { @"c:\functions\run.py", new MockFileData(string.Empty) },
                { @"c:\functions\queueTrigger.py", new MockFileData(string.Empty) },
                { @"c:\functions\helper.py", new MockFileData(string.Empty) },
                { @"c:\functions\test.txt", new MockFileData(string.Empty) }
            };
            var fileSystem = new MockFileSystem(files);

            string scriptFile = ScriptHost.DeterminePrimaryScriptFile(functionConfig, @"c:\functions", fileSystem);
            Assert.Equal(@"c:\functions\queueTrigger.py", scriptFile);
        }

        [Fact]
        public void DeterminePrimaryScriptFile_MultipleFiles_NoClearPrimary_ReturnsNull()
        {
            var functionConfig = new JObject();
            var files = new Dictionary<string, MockFileData>
            {
                { @"c:\functions\foo.py", new MockFileData(string.Empty) },
                { @"c:\functions\queueTrigger.py", new MockFileData(string.Empty) },
                { @"c:\functions\helper.py", new MockFileData(string.Empty) },
                { @"c:\functions\test.txt", new MockFileData(string.Empty) }
            };
            var fileSystem = new MockFileSystem(files);
            Assert.Throws<ConfigurationErrorsException>(() => ScriptHost.DeterminePrimaryScriptFile(functionConfig, @"c:\functions", fileSystem));
        }

        [Fact]
        public void DeterminePrimaryScriptFile_NoFiles_ReturnsNull()
        {
            var functionConfig = new JObject();
            string[] functionFiles = new string[0];
            var fileSystem = new MockFileSystem();
            fileSystem.AddDirectory(@"c:\functions");
            Assert.Throws<ConfigurationErrorsException>(() => ScriptHost.DeterminePrimaryScriptFile(functionConfig, @"c:\functions", fileSystem));
        }

        [Fact]
        public void DeterminePrimaryScriptFile_MultipleFiles_RunFilePresent()
        {
            var functionConfig = new JObject();
            var files = new Dictionary<string, MockFileData>
            {
                { @"c:\functions\Run.csx", new MockFileData(string.Empty) },
                { @"c:\functions\Helper.csx", new MockFileData(string.Empty) },
                { @"c:\functions\test.txt", new MockFileData(string.Empty) }
            };
            var fileSystem = new MockFileSystem(files);
            string scriptFile = ScriptHost.DeterminePrimaryScriptFile(functionConfig, @"c:\functions", fileSystem);
            Assert.Equal(@"c:\functions\Run.csx", scriptFile);
        }

        [Fact]
        public void DeterminePrimaryScriptFile_SingleFile()
        {
            var functionConfig = new JObject();
            var files = new Dictionary<string, MockFileData>
            {
                { @"c:\functions\Run.csx", new MockFileData(string.Empty) }
            };
            var fileSystem = new MockFileSystem(files);
            string scriptFile = ScriptHost.DeterminePrimaryScriptFile(functionConfig, @"c:\functions", fileSystem);
            Assert.Equal(@"c:\functions\Run.csx", scriptFile);
        }

        [Fact]
        public void DeterminePrimaryScriptFile_MultipleFiles_RunTrumpsIndex()
        {
            var functionConfig = new JObject();
            var files = new Dictionary<string, MockFileData>
            {
                { @"c:\functions\run.js", new MockFileData(string.Empty) },
                { @"c:\functions\index.js", new MockFileData(string.Empty) },
                { @"c:\functions\test.txt", new MockFileData(string.Empty) }
            };
            var fileSystem = new MockFileSystem(files);
            string scriptFile = ScriptHost.DeterminePrimaryScriptFile(functionConfig, @"c:\functions", fileSystem);
            Assert.Equal(@"c:\functions\run.js", scriptFile);
        }

        [Fact]
        public void DeterminePrimaryScriptFile_MultipleFiles_IndexFilePresent()
        {
            var functionConfig = new JObject();
            var files = new Dictionary<string, MockFileData>
            {
                { @"c:\functions\index.js", new MockFileData(string.Empty) },
                { @"c:\functions\test.txt", new MockFileData(string.Empty) }
            };
            var fileSystem = new MockFileSystem(files);
            string scriptFile = ScriptHost.DeterminePrimaryScriptFile(functionConfig, @"c:\functions", fileSystem);
            Assert.Equal(@"c:\functions\index.js", scriptFile);
        }

        [Theory]
        [InlineData("host")]
        [InlineData("-function")]
        [InlineData("_function")]
        [InlineData("function test")]
        [InlineData("function.test")]
        [InlineData("function0.1")]
        public void Create_InvalidFunctionNames_DoesNotCreateFunctionAndLogsFailure(string functionName)
        {
            string rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            string invalidFunctionNamePath = Path.Combine(rootPath, functionName);
            try
            {
                Directory.CreateDirectory(invalidFunctionNamePath);

                File.WriteAllText(Path.Combine(invalidFunctionNamePath, ScriptConstants.FunctionMetadataFileName), string.Empty);

                ScriptHostConfiguration scriptConfig = new ScriptHostConfiguration.Builder()
                    .WithHostId(ID)
                    .WithRootScriptPath(rootPath)
                    .Build();
                var environment = new Mock<IScriptHostEnvironment>();
                var eventManager = new Mock<IScriptEventManager>();

                var scriptHost = ScriptHost.Create(environment.Object, eventManager.Object, scriptConfig, _settingsManager);

                Assert.Equal(1, scriptHost.FunctionErrors.Count);
                Assert.Equal(functionName, scriptHost.FunctionErrors.First().Key);
                Assert.Equal($"'{functionName}' is not a valid function name.", scriptHost.FunctionErrors.First().Value.First());
            }
            finally
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, true);
                }
            }
        }

        // TODO: Move this test into a new WebJobsCoreScriptBindingProvider class since
        // the functionality moved. Also add tests for the ServiceBus config, etc.
        [Fact]
        public void ApplyConfiguration_Queues()
        {
            JObject config = new JObject();
            config["id"] = ID;
            JObject queuesConfig = new JObject();
            config["queues"] = queuesConfig;

            ScriptHostConfiguration scriptConfig = new ScriptHostConfiguration();
            scriptConfig.HostConfig.HostConfigMetadata = config;
            TraceWriter traceWriter = new TestTraceWriter(TraceLevel.Verbose);

            scriptConfig.HostConfig.CreateMetadataProvider(); // will cause extensions to initialize and consume config metadata.

            Assert.Equal(60 * 1000, scriptConfig.HostConfig.Queues.MaxPollingInterval.TotalMilliseconds);
            Assert.Equal(16, scriptConfig.HostConfig.Queues.BatchSize);
            Assert.Equal(5, scriptConfig.HostConfig.Queues.MaxDequeueCount);
            Assert.Equal(8, scriptConfig.HostConfig.Queues.NewBatchThreshold);
            Assert.Equal(TimeSpan.Zero, scriptConfig.HostConfig.Queues.VisibilityTimeout);

            queuesConfig["maxPollingInterval"] = 5000;
            queuesConfig["batchSize"] = 17;
            queuesConfig["maxDequeueCount"] = 3;
            queuesConfig["newBatchThreshold"] = 123;
            queuesConfig["visibilityTimeout"] = "00:00:30";

            scriptConfig = new ScriptHostConfiguration();
            scriptConfig.HostConfig.HostConfigMetadata = config;
            scriptConfig.HostConfig.CreateMetadataProvider(); // will cause extensions to initialize and consume config metadata.

            Assert.Equal(5000, scriptConfig.HostConfig.Queues.MaxPollingInterval.TotalMilliseconds);
            Assert.Equal(17, scriptConfig.HostConfig.Queues.BatchSize);
            Assert.Equal(3, scriptConfig.HostConfig.Queues.MaxDequeueCount);
            Assert.Equal(123, scriptConfig.HostConfig.Queues.NewBatchThreshold);
            Assert.Equal(TimeSpan.FromSeconds(30), scriptConfig.HostConfig.Queues.VisibilityTimeout);
        }

        [Fact]
        public void ApplyConfiguration_Http()
        {
            JObject config = new JObject();
            config["id"] = ID;
            JObject http = new JObject();
            config["http"] = http;

            JobHostConfiguration hostConfig = new JobHostConfiguration();
            TraceWriter traceWriter = new TestTraceWriter(TraceLevel.Verbose);
            WebJobsCoreScriptBindingProvider provider = new WebJobsCoreScriptBindingProvider(hostConfig, config, traceWriter);
            provider.Initialize();

            IExtensionRegistry extensions = hostConfig.GetService<IExtensionRegistry>();
            var httpConfig = extensions.GetExtensions<IExtensionConfigProvider>().OfType<HttpExtensionConfiguration>().Single();

            Assert.Equal(HttpExtensionConstants.DefaultRoutePrefix, httpConfig.RoutePrefix);
            Assert.Equal(false, httpConfig.DynamicThrottlesEnabled);
            Assert.Equal(DataflowBlockOptions.Unbounded, httpConfig.MaxConcurrentRequests);
            Assert.Equal(DataflowBlockOptions.Unbounded, httpConfig.MaxOutstandingRequests);

            http["routePrefix"] = "myprefix";
            http["dynamicThrottlesEnabled"] = true;
            http["maxConcurrentRequests"] = 5;
            http["maxOutstandingRequests"] = 10;

            hostConfig = new JobHostConfiguration();
            provider = new WebJobsCoreScriptBindingProvider(hostConfig, config, traceWriter);
            provider.Initialize();

            extensions = hostConfig.GetService<IExtensionRegistry>();
            httpConfig = extensions.GetExtensions<IExtensionConfigProvider>().OfType<HttpExtensionConfiguration>().Single();

            Assert.Equal("myprefix", httpConfig.RoutePrefix);
            Assert.Equal(true, httpConfig.DynamicThrottlesEnabled);
            Assert.Equal(5, httpConfig.MaxConcurrentRequests);
            Assert.Equal(10, httpConfig.MaxOutstandingRequests);
        }

        [Fact]
        public void ApplyConfiguration_Blobs()
        {
            JObject config = new JObject();
            config["id"] = ID;
            JObject blobsConfig = new JObject();
            config["blobs"] = blobsConfig;

            JobHostConfiguration hostConfig = new JobHostConfiguration();
            TraceWriter traceWriter = new TestTraceWriter(TraceLevel.Verbose);

            WebJobsCoreScriptBindingProvider provider = new WebJobsCoreScriptBindingProvider(hostConfig, config, new TestTraceWriter(TraceLevel.Verbose));
            provider.Initialize();

            Assert.True(hostConfig.Blobs.CentralizedPoisonQueue);

            blobsConfig["centralizedPoisonQueue"] = false;

            provider = new WebJobsCoreScriptBindingProvider(hostConfig, config, new TestTraceWriter(TraceLevel.Verbose));
            provider.Initialize();

            Assert.False(hostConfig.Blobs.CentralizedPoisonQueue);
        }

        [Fact]
        public void TryGetFunctionFromException_FunctionMatch()
        {
            string stack = "TypeError: Cannot read property 'is' of undefined\n" +
                           "at Timeout._onTimeout(D:\\home\\site\\wwwroot\\HttpTriggerNode\\index.js:7:35)\n" +
                           "at tryOnTimeout (timers.js:224:11)\n" +
                           "at Timer.listOnTimeout(timers.js:198:5)";
            Collection<FunctionDescriptor> functions = new Collection<FunctionDescriptor>();
            var exception = new InvalidOperationException(stack);

            // no match - empty functions
            FunctionDescriptor functionResult = null;
            bool result = ScriptHost.TryGetFunctionFromException(functions, exception, out functionResult);
            Assert.False(result);
            Assert.Null(functionResult);

            // no match - one non-matching function
            FunctionMetadata metadata = new FunctionMetadata
            {
                Name = "SomeFunction",
                ScriptFile = "D:\\home\\site\\wwwroot\\SomeFunction\\index.js"
            };
            FunctionDescriptor function = new FunctionDescriptor("TimerFunction", new TestInvoker(), metadata, new Collection<ParameterDescriptor>(), null, null, null);
            functions.Add(function);
            result = ScriptHost.TryGetFunctionFromException(functions, exception, out functionResult);
            Assert.False(result);
            Assert.Null(functionResult);

            // match - exact
            metadata = new FunctionMetadata
            {
                Name = "HttpTriggerNode",
                ScriptFile = "D:\\home\\site\\wwwroot\\HttpTriggerNode\\index.js"
            };
            function = new FunctionDescriptor("TimerFunction", new TestInvoker(), metadata, new Collection<ParameterDescriptor>(), null, null, null);
            functions.Add(function);
            result = ScriptHost.TryGetFunctionFromException(functions, exception, out functionResult);
            Assert.True(result);
            Assert.Same(function, functionResult);

            // match - different file from the same function
            stack = "TypeError: Cannot read property 'is' of undefined\n" +
                           "at Timeout._onTimeout(D:\\home\\site\\wwwroot\\HttpTriggerNode\\npm\\lib\\foo.js:7:35)\n" +
                           "at tryOnTimeout (timers.js:224:11)\n" +
                           "at Timer.listOnTimeout(timers.js:198:5)";
            exception = new InvalidOperationException(stack);
            result = ScriptHost.TryGetFunctionFromException(functions, exception, out functionResult);
            Assert.True(result);
            Assert.Same(function, functionResult);
        }

        [Theory]
        [InlineData("")]
        [InlineData("host")]
        [InlineData("Host")]
        [InlineData("-function")]
        [InlineData("_function")]
        [InlineData("function test")]
        [InlineData("function.test")]
        [InlineData("function0.1")]
        public void ValidateFunctionName_ThrowsOnInvalidName(string functionName)
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                ScriptHost.ValidateFunctionName(functionName);
            });

            Assert.Equal(string.Format("'{0}' is not a valid function name.", functionName), ex.Message);
        }

        [Theory]
        [InlineData("testwithhost")]
        [InlineData("hosts")]
        [InlineData("myfunction")]
        [InlineData("myfunction-test")]
        [InlineData("myfunction_test")]
        public void ValidateFunctionName_DoesNotThrowOnValidName(string functionName)
        {
            try
            {
                ScriptHost.ValidateFunctionName(functionName);
            }
            catch (InvalidOperationException)
            {
                Assert.True(false, $"Valid function name {functionName} failed validation.");
            }
        }

        [Fact]
        public void HttpRoutesConflict_ReturnsExpectedResult()
        {
            var first = new HttpTriggerAttribute
            {
                Route = "foo/bar/baz"
            };
            var second = new HttpTriggerAttribute
            {
                Route = "foo/bar"
            };
            Assert.False(ScriptHost.HttpRoutesConflict(first, second));
            Assert.False(ScriptHost.HttpRoutesConflict(second, first));

            first = new HttpTriggerAttribute
            {
                Route = "foo/bar/baz"
            };
            second = new HttpTriggerAttribute
            {
                Route = "foo/bar/baz"
            };
            Assert.True(ScriptHost.HttpRoutesConflict(first, second));
            Assert.True(ScriptHost.HttpRoutesConflict(second, first));

            // no conflict since methods do not intersect
            first = new HttpTriggerAttribute(AuthorizationLevel.Function, "get", "head")
            {
                Route = "foo/bar/baz"
            };
            second = new HttpTriggerAttribute(AuthorizationLevel.Function, "post", "put")
            {
                Route = "foo/bar/baz"
            };
            Assert.False(ScriptHost.HttpRoutesConflict(first, second));
            Assert.False(ScriptHost.HttpRoutesConflict(second, first));

            first = new HttpTriggerAttribute(AuthorizationLevel.Function, "get", "head")
            {
                Route = "foo/bar/baz"
            };
            second = new HttpTriggerAttribute
            {
                Route = "foo/bar/baz"
            };
            Assert.True(ScriptHost.HttpRoutesConflict(first, second));
            Assert.True(ScriptHost.HttpRoutesConflict(second, first));

            first = new HttpTriggerAttribute(AuthorizationLevel.Function, "get", "head", "put", "post")
            {
                Route = "foo/bar/baz"
            };
            second = new HttpTriggerAttribute(AuthorizationLevel.Function, "put")
            {
                Route = "foo/bar/baz"
            };
            Assert.True(ScriptHost.HttpRoutesConflict(first, second));
            Assert.True(ScriptHost.HttpRoutesConflict(second, first));
        }

        [Fact]
        public void ValidateFunction_ValidatesHttpRoutes()
        {
            var httpFunctions = new Dictionary<string, HttpTriggerAttribute>();

            // first add an http function
            var function = new Mock<FunctionDescriptor>(MockBehavior.Strict, "test", null, null, null, null, null, null);
            var attribute = new HttpTriggerAttribute(AuthorizationLevel.Function, "get")
            {
                Route = "products/{category}/{id?}"
            };
            function.Setup(p => p.GetTriggerAttributeOrNull<HttpTriggerAttribute>()).Returns(() => attribute);
            ScriptHost.ValidateFunction(function.Object, httpFunctions);
            Assert.Equal(1, httpFunctions.Count);
            Assert.True(httpFunctions.ContainsKey("test"));

            // add another for a completely different route
            function = new Mock<FunctionDescriptor>(MockBehavior.Strict, "test2", null, null, null, null, null, null);
            attribute = new HttpTriggerAttribute(AuthorizationLevel.Function, "get")
            {
                Route = "/foo/bar/baz/"
            };
            function.Setup(p => p.GetTriggerAttributeOrNull<HttpTriggerAttribute>()).Returns(() => attribute);
            ScriptHost.ValidateFunction(function.Object, httpFunctions);
            Assert.Equal(2, httpFunctions.Count);
            Assert.True(httpFunctions.ContainsKey("test2"));

            // add another that varies from another only by http methods
            function = new Mock<FunctionDescriptor>(MockBehavior.Strict, "test3", null, null, null, null, null, null);
            attribute = new HttpTriggerAttribute(AuthorizationLevel.Function, "put", "post")
            {
                Route = "/foo/bar/baz/"
            };
            function.Setup(p => p.GetTriggerAttributeOrNull<HttpTriggerAttribute>()).Returns(() => attribute);
            ScriptHost.ValidateFunction(function.Object, httpFunctions);
            Assert.Equal(3, httpFunctions.Count);
            Assert.True(httpFunctions.ContainsKey("test3"));

            // now try to add a function for the same route
            // where the http methods overlap
            function = new Mock<FunctionDescriptor>(MockBehavior.Strict, "test4", null, null, null, null, null, null);
            attribute = new HttpTriggerAttribute
            {
                Route = "/foo/bar/baz/"
            };
            function.Setup(p => p.GetTriggerAttributeOrNull<HttpTriggerAttribute>()).Returns(() => attribute);
            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                ScriptHost.ValidateFunction(function.Object, httpFunctions);
            });
            Assert.Equal("The route specified conflicts with the route defined by function 'test2'.", ex.Message);
            Assert.Equal(3, httpFunctions.Count);

            // try to add a route under reserved admin route
            function = new Mock<FunctionDescriptor>(MockBehavior.Strict, "test5", null, null, null, null, null, null);
            attribute = new HttpTriggerAttribute
            {
                Route = "admin/foo/bar"
            };
            function.Setup(p => p.GetTriggerAttributeOrNull<HttpTriggerAttribute>()).Returns(() => attribute);
            ex = Assert.Throws<InvalidOperationException>(() =>
            {
                ScriptHost.ValidateFunction(function.Object, httpFunctions);
            });
            Assert.Equal("The specified route conflicts with one or more built in routes.", ex.Message);

            // verify that empty route is defaulted to function name
            function = new Mock<FunctionDescriptor>(MockBehavior.Strict, "test6", null, null, null, null, null, null);
            attribute = new HttpTriggerAttribute();
            function.Setup(p => p.GetTriggerAttributeOrNull<HttpTriggerAttribute>()).Returns(() => attribute);
            ScriptHost.ValidateFunction(function.Object, httpFunctions);
            Assert.Equal(4, httpFunctions.Count);
            Assert.True(httpFunctions.ContainsKey("test6"));
            Assert.Equal("test6", attribute.Route);
        }

        [Fact]
        public void IsFunction_ReturnsExpectedResult()
        {
            Mock<IScriptHostEnvironment> mockEnvironment = new Mock<IScriptHostEnvironment>(MockBehavior.Strict);
            var config = new ScriptHostConfiguration();
            var eventManager = new Mock<IScriptEventManager>();
            var mockHost = new Mock<ScriptHost>(MockBehavior.Strict, new object[] { mockEnvironment.Object, eventManager.Object, config, null });

            var functions = new Collection<FunctionDescriptor>();
            var functionErrors = new Dictionary<string, Collection<string>>();
            mockHost.Setup(p => p.Functions).Returns(functions);
            mockHost.Setup(p => p.FunctionErrors).Returns(functionErrors);

            var parameters = new Collection<ParameterDescriptor>();
            parameters.Add(new ParameterDescriptor("param1", typeof(string)));
            var metadata = new FunctionMetadata();
            var invoker = new TestInvoker();
            var function = new FunctionDescriptor("TestFunction", invoker, metadata, parameters, null, null, null);
            functions.Add(function);

            var errors = new Collection<string>();
            errors.Add("A really really bad error!");
            functionErrors.Add("ErrorFunction", errors);

            var host = mockHost.Object;
            Assert.True(host.IsFunction("TestFunction"));
            Assert.True(host.IsFunction("ErrorFunction"));
            Assert.False(host.IsFunction("DoesNotExist"));
            Assert.False(host.IsFunction(string.Empty));
            Assert.False(host.IsFunction(null));
        }

        [Fact]
        public void GetDefaultHostId_SelfHost_ReturnsExpectedResult()
        {
            var config = new ScriptHostConfiguration.Builder()
            .WithSelfHostValue(true)
            .WithRootScriptPath(@"c:\testing\FUNCTIONS-TEST\test$#")
            .Build();

            var scriptSettingsManagerMock = new Mock<ScriptSettingsManager>(MockBehavior.Strict);

            string hostId = Utility.GetDefaultHostId(scriptSettingsManagerMock.Object, config.IsSelfHost, config.RootScriptPath);
            string sanitizedMachineName = Environment.MachineName
                    .Where(char.IsLetterOrDigit)
                    .Aggregate(new StringBuilder(), (b, c) => b.Append(c)).ToString().ToLowerInvariant();
            Assert.Equal($"{sanitizedMachineName}-789851553", hostId);
        }

        public class AssemblyMock : Assembly
        {
            public override object[] GetCustomAttributes(Type attributeType, bool inherit)
            {
                return new Attribute[] { };
            }
        }

        public class TestFixture
        {
            public TestFixture()
            {
                ScriptHostConfiguration config = new ScriptHostConfiguration();
                config.HostConfig.HostId = ID;
                var environment = new Mock<IScriptHostEnvironment>();
                var eventManager = new Mock<IScriptEventManager>();
                Host = ScriptHost.Create(environment.Object, eventManager.Object, config);
            }

            public ScriptHost Host { get; private set; }
        }

        private class TestLoggerFactory : ILoggerFactory
        {
            public TestLoggerFactory()
            {
                Providers = new List<ILoggerProvider>();
            }

            public IList<ILoggerProvider> Providers { get; private set; }

            public void AddProvider(ILoggerProvider provider)
            {
                Providers.Add(provider);
            }

            public ILogger CreateLogger(string categoryName)
            {
                throw new NotImplementedException();
            }

            public void Dispose()
            {
            }
        }
    }
}
