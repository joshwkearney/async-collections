﻿using System.Runtime.CompilerServices;

namespace AsyncCollections.Linq;

public static partial class AsyncEnumerable {
    public static IAsyncEnumerable<T> AsyncAppend<T>(this IAsyncEnumerable<T> sequence, Func<ValueTask<T>> newItemTask) {
        if (sequence is IAsyncEnumerableOperator<T> op) {
            if (op.ExecutionMode == AsyncExecutionMode.Sequential) {
                return new AsyncAppendOperator<T>(op, newItemTask);
            }
            if (op.ExecutionMode == AsyncExecutionMode.Parallel) {
                var task = Task.Run(() => newItemTask().AsTask());

                return sequence.Concat(task.AsAsyncEnumerable());
            }
            else {
                return sequence.Concat(newItemTask().AsAsyncEnumerable());
            }
        }

        return AsyncAppendHelper(sequence, newItemTask);
    }

    private static async IAsyncEnumerable<T> AsyncAppendHelper<T>(
        this IAsyncEnumerable<T> sequence, 
        Func<ValueTask<T>> newItem,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {

        await foreach (var item in sequence.WithCancellation(cancellationToken)) {
            yield return item;
        }

        yield return await newItem();
    }

    private static async IAsyncEnumerable<T> ConcurrentAsyncAppendHelper<T>(
        this IAsyncEnumerable<T> sequence, 
        Func<ValueTask<T>> newItem,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {

        var newItemTask = newItem();
        
        await foreach (var item in sequence.WithCancellation(cancellationToken)) {
            yield return item;
        }

        yield return await newItemTask;
    }

    private static async IAsyncEnumerable<T> ParallelAsyncAppendHelper<T>(
        this IAsyncEnumerable<T> sequence, 
        Func<ValueTask<T>> newItem,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {

        var newItemTask = Task.Run(async () => await newItem());

        await foreach (var item in sequence.WithCancellation(cancellationToken)) {
            yield return item;
        }

        yield return await newItemTask;
    }

    private class AsyncAppendOperator<T> : IAsyncEnumerableOperator<T> {
        private readonly IAsyncEnumerableOperator<T> parent;
        private readonly Func<ValueTask<T>> toAppend;

        public AsyncAppendOperator(IAsyncEnumerableOperator<T> parent, Func<ValueTask<T>> item) {
            this.parent = parent;
            this.toAppend = item;
            this.ExecutionMode = this.parent.ExecutionMode;
        }
        
        public AsyncExecutionMode ExecutionMode { get; }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
            if (this.ExecutionMode == AsyncExecutionMode.Concurrent) {
                return ConcurrentAsyncAppendHelper(this.parent, this.toAppend).GetAsyncEnumerator(cancellationToken);
            }
            else if (this.ExecutionMode == AsyncExecutionMode.Parallel) {
                return ParallelAsyncAppendHelper(this.parent, this.toAppend).GetAsyncEnumerator(cancellationToken);
            }
            else {
                return AsyncAppendHelper(this.parent, this.toAppend).GetAsyncEnumerator(cancellationToken);
            }
        }
    }
}