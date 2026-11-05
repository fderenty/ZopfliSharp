// ZopfliSharp: Zopfli compression in C#. https://github.com/fderenty/ZopfliSharp
//
// Not in the original, whose compression runs on one thread.
//
// Licensed under the Apache License, Version 2.0: see LICENSE and NOTICE.

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace ZopfliSharp.Internal;

/// <summary>
/// Runs the independent parts of one compression on several threads, within a budget shared
/// by every level of nesting: master blocks, the blocks within one, the candidate split points
/// of a block. A loop takes helpers from the budget only while some are left, so the calling
/// thread plus its helpers never exceed the degree asked for, however the loops nest.
/// </summary>
/// <remarks>
/// Only work whose results are combined in a fixed order afterwards goes through here, so the
/// output never depends on the number of threads or on which one finishes first.
/// </remarks>
internal sealed class Parallelism
{
    private readonly bool _unbounded;
    private readonly CancellationToken _cancellationToken;
    private int _spareHelpers;

    private Parallelism(int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        _unbounded = maxDegreeOfParallelism < 0;
        _spareHelpers = maxDegreeOfParallelism - 1;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Null for 1: the plain loops then run, with no delegate or task in the way.</summary>
    public static Parallelism? Create(int maxDegreeOfParallelism, CancellationToken cancellationToken)
        => maxDegreeOfParallelism == 1 ? null : new Parallelism(maxDegreeOfParallelism, cancellationToken);

    /// <summary>
    /// The number of master blocks worth planning ahead of the writer: enough to keep every
    /// thread busy, few enough that their LZ77 data does not pile up.
    /// </summary>
    public int BatchSize => _unbounded ? Environment.ProcessorCount : Math.Max(1, Volatile.Read(ref _spareHelpers) + 1);

    /// <summary>Runs <paramref name="body"/> for 0 to <paramref name="count"/> - 1, in any order.</summary>
    public static void For(Parallelism? parallelism, int count, Action<int> body)
    {
        if (parallelism is null)
        {
            for (int i = 0; i < count; i++)
                body(i);
        }
        else
        {
            parallelism.For(count, body);
        }
    }

    private void For(int count, Action<int> body)
    {
        if (count <= 0)
            return;

        int helpers = Acquire(count - 1);
        try
        {
            if (helpers == 0)
            {
                for (int i = 0; i < count; i++)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    body(i);
                }
                return;
            }

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = _unbounded ? -1 : helpers + 1,
                CancellationToken = _cancellationToken,
            };

            try
            {
                Parallel.For(0, count, options, body);
            }
            catch (AggregateException e)
            {
                // A cancellation seen inside a body arrives wrapped; callers expect it bare,
                // as from the sequential path. Anything else is rethrown as it was thrown.
                _cancellationToken.ThrowIfCancellationRequested();
                ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            }
        }
        finally
        {
            Release(helpers);
        }
    }

    private int Acquire(int wanted)
    {
        if (_unbounded)
            return wanted;

        while (true)
        {
            int spare = Volatile.Read(ref _spareHelpers);
            int take = Math.Min(spare, wanted);
            if (take <= 0)
                return 0;
            if (Interlocked.CompareExchange(ref _spareHelpers, spare - take, spare) == spare)
                return take;
        }
    }

    private void Release(int helpers)
    {
        if (!_unbounded && helpers > 0)
            Interlocked.Add(ref _spareHelpers, helpers);
    }
}

/// <summary>
/// Workspaces for the threads of one compression. Each piece of work rents one for its
/// duration; with a single thread the same one serves every pass, as before.
/// </summary>
internal sealed class WorkspacePool
{
    private readonly ConcurrentBag<Workspace> _free = [];

    public Workspace Rent() => _free.TryTake(out Workspace? workspace) ? workspace : new Workspace();

    public void Return(Workspace workspace) => _free.Add(workspace);
}
