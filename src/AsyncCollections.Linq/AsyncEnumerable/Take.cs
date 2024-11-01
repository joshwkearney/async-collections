﻿using System.Runtime.CompilerServices;

namespace AsyncCollections.Linq;

public static partial class AsyncEnumerable {
    public static IAsyncEnumerable<T> Take<T>(this IAsyncEnumerable<T> sequence, int numToTake) {
        if (numToTake < 0) {
            throw new ArgumentOutOfRangeException(nameof(numToTake), "Cannot take less than zero elements");
        }

        if (sequence is IAsyncEnumerableOperator<T> collection) {
            return new TakeOperator<T>(collection, numToTake);
        }

        return TakeHelper(sequence, numToTake);
    }

    private static async IAsyncEnumerable<T> TakeHelper<T>(
        this IAsyncEnumerable<T> sequence, 
        int numToTake,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {

        int taken = 0;

        await foreach (var item in sequence.WithCancellation(cancellationToken)) {
            if (taken >= numToTake) {
                yield break;
            }

            yield return item;
            taken++;
        }
    }

    private class TakeOperator<T> : IAsyncEnumerableOperator<T> {
        private readonly IAsyncEnumerableOperator<T> parent;
        private readonly int numToTake;
        
        public AsyncExecutionMode ExecutionMode => this.parent.ExecutionMode;

        public TakeOperator(IAsyncEnumerableOperator<T> parent, int numToTake) {
            this.parent = parent;
            this.numToTake = numToTake;
        }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
            return TakeHelper(this.parent, this.numToTake).GetAsyncEnumerator(cancellationToken);
        }
    }
}