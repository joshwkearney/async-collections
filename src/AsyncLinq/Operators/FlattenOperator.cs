﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AsyncLinq.Operators {
    internal interface IConcatOperator<T> : IAsyncOperator<T> {
        public IAsyncEnumerable<T> Concat(IAsyncEnumerable<T> sequence);
    }
    
    internal class FlattenOperator<T> : IAsyncOperator<T>, IConcatOperator<T>, IConcatEnumerablesOperator<T> {
        private static readonly UnboundedChannelOptions channelOptions = new UnboundedChannelOptions() {
            AllowSynchronousContinuations = true
        };

        private readonly IAsyncEnumerable<IAsyncEnumerable<T>> parent;

        public AsyncOperatorParams Params { get; }

        public FlattenOperator(AsyncOperatorParams pars, IAsyncEnumerable<IAsyncEnumerable<T>> parent) {
            this.parent = parent;
            Params = pars;
        }
        
        public IAsyncOperator<T> WithParams(AsyncOperatorParams pars) {
            return new FlattenOperator<T>(pars, parent);
        }
        
        public IAsyncEnumerable<T> Concat(IAsyncEnumerable<T> sequence) {
            if (this.parent is EnumerableOperator<IAsyncEnumerable<T>> op) {
                var newItems = op.Items.Append(sequence);
                var newParent = new EnumerableOperator<IAsyncEnumerable<T>>(op.Params, newItems);
                
                return new FlattenOperator<T>(this.Params, newParent);
            }
            else {
                return new FlattenOperator<T>(this.Params, new[] { this, sequence }.AsAsyncEnumerable());
            }
        }

        public IAsyncEnumerable<T> ConcatEnumerables(IEnumerable<T> before, IEnumerable<T> after) {
            if (this.parent is EnumerableOperator<IAsyncEnumerable<T>> op) {
                var newItems = op.Items.Prepend(before.AsAsyncEnumerable()).Append(after.AsAsyncEnumerable());
                var newParent = new EnumerableOperator<IAsyncEnumerable<T>>(op.Params, newItems);
                
                return new FlattenOperator<T>(this.Params, newParent);
            }
            else {
                return new ConcatEnumerablesOperator<T>(this.Params, this, before, after);
            }
        }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
            if (this.Params.ExecutionMode == AsyncExecutionMode.Sequential) {
                return this.SequentialHelper(cancellationToken);
            }
            else if (this.Params.IsUnordered) {
                return this.UnorderedHelper(cancellationToken);
            }
            else {
                return this.OrderedHelper(cancellationToken);
            }
        }        

        private async IAsyncEnumerator<T> SequentialHelper(CancellationToken cancellationToken) {
            await foreach (var item in this.parent.WithCancellation(cancellationToken)) {
                await foreach (var sub in item.WithCancellation(cancellationToken)) {
                    yield return sub;
                }
            }
        }

        private async IAsyncEnumerator<T> OrderedHelper(CancellationToken parentToken) {
            using var cancelSource = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            var channel = Channel.CreateUnbounded<Channel<T>>(channelOptions);
            var errors = new ErrorCollection();
            
            IterateOuter();

            while (true) {
                try {
                    // Don't pass a cancellation token here
                    var canRead = await channel.Reader.WaitToReadAsync();

                    if (!canRead) {
                        break;
                    }
                }
                catch (Exception ex) {
                    errors.Add(ex);
                    cancelSource.Cancel();
                    break;
                }

                if (!channel.Reader.TryRead(out var subChannel)) {
                    break;
                }

                while (true) {
                    try {
                        // Also don't pass a cancellation source here
                        var canReadSub = await subChannel.Reader.WaitToReadAsync();

                        if (!canReadSub) {
                            break;
                        }
                    }
                    catch (Exception ex) {
                        errors.Add(ex);
                        cancelSource.Cancel();
                        break;
                    }

                    if (!subChannel.Reader.TryRead(out var item)) {
                        break;
                    }

                    yield return item;
                }
            }
            
            var finalError = errors.ToException();

            if (finalError != null) {
                throw finalError;
            }

            async void IterateOuter() {
                try {
                    await foreach (var item in this.parent.WithCancellation(cancelSource.Token)) {
                        if (item == EmptyOperator<T>.Instance) {
                            continue;
                        }

                        var subChannel = Channel.CreateUnbounded<T>(channelOptions);
                        channel.Writer.TryWrite(subChannel);

                        IterateInner(item, subChannel);
                    }
                    
                    channel.Writer.Complete();
                }
                catch (Exception ex) {
                    channel.Writer.Complete(ex);
                }
            }

            async void IterateInner(IAsyncEnumerable<T> seq, Channel<T> subChannel) {
                try {
                    await foreach (var item in seq.WithCancellation(cancelSource.Token)) {
                        subChannel.Writer.TryWrite(item);
                    }

                    subChannel.Writer.Complete();
                }
                catch (Exception ex) {
                    subChannel.Writer.Complete(ex);
                }
            }
        }

        private async IAsyncEnumerator<T> UnorderedHelper(CancellationToken parentToken) {
            using var cancelSource = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            var channel = Channel.CreateUnbounded<T>(channelOptions);
            
            IterateOuter();

            while (true) {
                // Don't pass a cancellation token here, we want the channel to finish on its own
                var canRead = await channel.Reader.WaitToReadAsync();

                if (!canRead) {
                    break;
                }

                if (!channel.Reader.TryRead(out var item)) {
                    break;
                }

                yield return item;
            }

            async void IterateOuter() {
                var tasks = new List<Task>();
                var errors = new ErrorCollection();
                
                try {
                    await foreach (var item in this.parent.WithCancellation(cancelSource.Token)) {
                        if (item == EmptyOperator<T>.Instance) {
                            continue;
                        }

                        var task = IterateInner(item);

                        if (!task.IsCompletedSuccessfully) {
                            tasks.Add(task.AsTask());
                        }
                    }
                }
                catch (Exception ex) {
                    // Whoops, our async enumerable blew up. Catch it so we can handle errors from the
                    // async tasks that are already running
                    errors.Add(ex);
                    cancelSource.Cancel();
                }

                foreach (var task in tasks) {
                    if (task.Exception != null) {
                        errors.Add(task.Exception);
                        cancelSource.Cancel();

                        continue;
                    }

                    try {
                        await task;
                    }
                    catch (Exception ex) {
                        errors.Add(ex);
                        cancelSource.Cancel();
                    }
                }

                var finalError = errors.ToException();

                if (finalError == null) {
                    channel.Writer.Complete();
                }
                else {
                    channel.Writer.Complete(finalError);
                }
            }

            async ValueTask IterateInner(IAsyncEnumerable<T> seq) {               
                await foreach (var item in seq.WithCancellation(cancelSource.Token)) {
                    channel.Writer.TryWrite(item);
                }
            }
        }
    }
}