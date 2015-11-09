﻿// Copyright 2015 ThoughtWorks, Inc.

// This file is part of Gauge-CSharp.

// Gauge-CSharp is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// Gauge-CSharp is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with Gauge-CSharp.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Gauge.Messages;
using Google.ProtocolBuffers;
using NLog;

namespace Gauge.CSharp.Runner
{
    public class MethodExecutor : IMethodExecutor
    {
        private readonly ISandbox _sandbox;
        private static readonly Logger Logger = LogManager.GetLogger("Sandbox");

        public MethodExecutor(ISandbox sandbox)
        {
            _sandbox = sandbox;
        }

        [DebuggerHidden]
        public ProtoExecutionResult Execute(MethodInfo method, params object[] args)
        {
            Logger.Debug("Execution method: {0}.{1}", method.DeclaringType.FullName, method.Name);
            var stopwatch = Stopwatch.StartNew();
            IEnumerable<string> pendingMessages = new List<string>();
            try
            {
                try
                {
                    _sandbox.ExecuteMethod(method, args);
                }
                catch (TargetInvocationException e)
                {
                    // Throw inner exception, which is the exception that matters to the user
                    // This is the exception that is thrown by the user's code
                    // and is fixable from the Step Implemented
                    ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                }
                finally
                {
                    pendingMessages = _sandbox.GetAllPendingMessages();
                }
                
                var builder = ProtoExecutionResult.CreateBuilder().SetFailed(false)
                                .SetExecutionTime(stopwatch.ElapsedMilliseconds);
                foreach (var message in pendingMessages)
                {
                    builder.AddMessage(message);   
                }
                return builder.Build();
            }
            catch (Exception e)
            {
                Logger.Error(e, "Error executing {0}.{1}", method.DeclaringType.FullName, method.Name);

                var elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                var builder = ProtoExecutionResult.CreateBuilder().SetFailed(true);
                var isScreenShotEnabled = Environment.GetEnvironmentVariable("screenshot_enabled");
                if (isScreenShotEnabled == null || isScreenShotEnabled.ToLower() != "false")
                {
                    builder.SetScreenShot(TakeScreenshot());
                }
                builder.SetErrorMessage(e.Message);
                builder.SetStackTrace(e.StackTrace);
                builder.SetRecoverableError(false);
                builder.SetExecutionTime(elapsedMilliseconds);
                foreach (var message in pendingMessages)
                {
                    builder.AddMessage(message);
                }
                return builder.Build();
            }
        }

        public void ClearCache()
        {
            _sandbox.ClearObjectCache();
        }

        private ByteString TakeScreenshot()
        {
            try
            {
                byte[] screenShotBytes;
                if (_sandbox.TryScreenCapture(out screenShotBytes))
                    return ByteString.CopyFrom(screenShotBytes);

                var bounds = Screen.GetBounds(Point.Empty);
                using (var bitmap = new Bitmap(bounds.Width, bounds.Height))
                {
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        g.CopyFromScreen(Point.Empty, Point.Empty, bounds.Size);
                    }
                    var memoryStream = new MemoryStream();
                    bitmap.Save(memoryStream, ImageFormat.Png);
                    var takeScreenshot = ByteString.CopyFrom(memoryStream.ToArray());
                    return takeScreenshot;
                }
            }
            catch
            {
                return ByteString.Empty;
            }
        }

        [DebuggerHidden]
        public ProtoExecutionResult ExecuteHooks(IEnumerable<MethodInfo> methods, ExecutionInfo executionInfo)
        {
            var stopwatch = Stopwatch.StartNew();
            foreach (var method in methods)
            {
                var executionResult = ExecuteHook(method, new object[] {executionInfo});
                if (!executionResult.Failed) continue;

                Logger.Debug("Hook execution failed : {0}.{1}", method.DeclaringType.FullName, method.Name);
                return ProtoExecutionResult.CreateBuilder(executionResult)
                    .SetFailed(true)
                    .SetRecoverableError(false)
                    .SetErrorMessage(executionResult.ErrorMessage)
                    .SetScreenShot(executionResult.ScreenShot)
                    .SetStackTrace(executionResult.StackTrace)
                    .SetExecutionTime(stopwatch.ElapsedMilliseconds)
                    .Build();
            }
            return ProtoExecutionResult.CreateBuilder()
                .SetFailed(false)
                .SetExecutionTime(stopwatch.ElapsedMilliseconds)
                .Build();
        }

        [DebuggerHidden]
        private ProtoExecutionResult ExecuteHook(MethodInfo method, object[] objects)
        {
            return HasArguments(method, objects) ? Execute(method, objects) : Execute(method);
        }

        private static bool HasArguments(MethodInfo method, object[] args)
        {
            if (method.GetParameters().Length != args.Length)
            {
                return false;
            }
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].GetType() != method.GetParameters()[i].ParameterType)
                {
                    return false;
                }
            }
            return true;
        }
    }
}