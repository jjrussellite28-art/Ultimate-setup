using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using CouchHud.Privileged;
using CouchHud.Service.Authorization;
using static CouchHud.Service.Pipe.NativeMethods;

namespace CouchHud.Service.Pipe;

/// <summary>The pipe name exists already, created by someone else: squatting suspected. The service must not run (fail-closed).</summary>
public sealed class PipeSquattedException(string message, int win32Error) : Exception(message)
{
    public int Win32Error { get; } = win32Error;
}

/// <summary>
/// Creates the instances of the service pipe natively (CreateNamedPipeW), because .NET sets neither FILE_FLAG_FIRST_PIPE_INSTANCE
/// nor PIPE_REJECT_REMOTE_CLIENTS and cannot apply the label SACL as SYSTEM (P0 spike). Every instance: message mode, remote
/// clients rejected, the security descriptor from <see cref="ServiceHardening.PipeSddl"/>. The first one also with
/// FILE_FLAG_FIRST_PIPE_INSTANCE: if the name exists already, that fails, and the service stops instead of joining a squatter's pipe.
/// </summary>
public sealed class PipeFactory(string pipeName, string sddl, int maxInstances)
{
    // A frame is at most the header and MaxMessageBytes.
    private const uint BufferBytes = Contracts.Protocol.MaxMessageBytes + 4;

    public string PipeName { get; } = pipeName;

    public NamedPipeServerStream Create(bool first)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "pipe security descriptor");
        }
        try
        {
            var attributes = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = descriptor,
                bInheritHandle = 0
            };
            var openMode = PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | (first ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0);
            const uint pipeMode = PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS;
            var handle = CreateNamedPipeW(@"\\.\pipe\" + PipeName, openMode, pipeMode, (uint)maxInstances, BufferBytes, BufferBytes, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                if (first && error is ERROR_ACCESS_DENIED or ERROR_PIPE_BUSY)
                {
                    throw new PipeSquattedException($"the pipe {PipeName} exists already (Win32 {error})", error);
                }
                throw new Win32Exception(error, "CreateNamedPipe");
            }
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }
}

/// <summary>
/// Accepts connections: one instance always listens, at most <see cref="ServiceHardening.MaxConnections"/> are being served. The
/// next instance is created before a connected one is handed off, so the pipe never has zero instances (a gap would let another
/// process create the name). If an instance cannot be created, the server stops (the service then stops with an error, the SCM
/// restarts it).
/// </summary>
public sealed class PipeServer(PipeFactory factory, ICallerIdentifier identifier, ConnectionHandler handler, IEventSink log, int maxConnections)
{
    public async Task RunAsync(CancellationToken cancellation)
    {
        var listening = factory.Create(first: true);   // PipeSquattedException goes to the caller
        log.Info($"[pipe] listening on {factory.PipeName}");
        // Not disposed: connections still finishing after the stop release their slot.
        var slots = new SemaphoreSlim(maxConnections, maxConnections);
        var running = new HashSet<Task>();
        var runningLock = new Lock();
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await slots.WaitAsync(cancellation).ConfigureAwait(false);
                var handedOff = false;
                try
                {
                    try
                    {
                        await listening.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // The client left before the connection was accepted: replace the instance (the new one first).
                        var fresh = factory.Create(first: false);
                        listening.Dispose();
                        listening = fresh;
                        continue;
                    }

                    var connected = listening;
                    listening = factory.Create(first: false);
                    handedOff = true;
                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            await ServeAsync(connected, cancellation).ConfigureAwait(false);
                        }
                        finally
                        {
                            slots.Release();
                        }
                    }, CancellationToken.None);
                    lock (runningLock) running.Add(task);
                    // Registered after the add, so the removal can never run before it.
                    _ = task.ContinueWith(done => { lock (runningLock) running.Remove(done); }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                finally
                {
                    if (!handedOff) slots.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            listening.Dispose();
            Task[] open;
            lock (runningLock) open = [.. running];
            await Task.WhenAny(Task.WhenAll(open), Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None)).ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellation)
    {
        try
        {
            // Synchronous on this thread: impersonate, read the token, revert.
            var caller = identifier.Identify(pipe.SafePipeHandle);
            await handler.HandleAsync(pipe, caller, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!cancellation.IsCancellationRequested) log.Error($"[pipe] connection ended with {ex.GetType().Name}");
        }
        finally
        {
            pipe.Dispose();
        }
    }
}
