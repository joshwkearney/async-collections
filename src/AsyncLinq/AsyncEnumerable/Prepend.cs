﻿using System.Runtime.CompilerServices;

namespace AsyncLinq;

public static partial class AsyncEnumerable {
    public static IAsyncEnumerable<TSource> Prepend<TSource>(
        this IAsyncEnumerable<TSource> source, 
        TSource element) {

        if (source == null) {
            throw new ArgumentNullException(nameof(source));
        }

        if (source is IAsyncLinqOperator<TSource> op) {
            if (op.ExecutionMode == AsyncLinqExecutionMode.Sequential) {
                return new PrependOperator<TSource>(op, element);
            }
            else {
                return source.Concat(new[] { element }.AsAsyncEnumerable());
            }
        }

        return PrependHelper(source, element);
    }

    private static async IAsyncEnumerable<T> PrependHelper<T>(
        this IAsyncEnumerable<T> sequence, 
        T newItem, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {

        // Create the iterator first so subscribe works correctly
        var iterator = sequence.WithCancellation(cancellationToken).GetAsyncEnumerator();
        var moveNextTask = iterator.MoveNextAsync();

        // Then yield the prepended item
        cancellationToken.ThrowIfCancellationRequested();
        yield return newItem;

        // Then yield the rest
        while (await moveNextTask) {
            yield return iterator.Current;
            moveNextTask = iterator.MoveNextAsync();
        }
    }

    private class PrependOperator<T> : IAsyncLinqOperator<T> {
        private readonly IAsyncLinqOperator<T> parent;
        private readonly T newItem;

        public PrependOperator(IAsyncLinqOperator<T> parent, T item) {
            this.parent = parent;
            this.newItem = item;
        }
        
        public AsyncLinqExecutionMode ExecutionMode => this.parent.ExecutionMode;

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
            return PrependHelper(this.parent, newItem).GetAsyncEnumerator(cancellationToken);
        }
    }
}