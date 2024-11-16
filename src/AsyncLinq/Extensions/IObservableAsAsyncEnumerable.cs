﻿using System.Threading.Channels;
using AsyncLinq.Operators;

namespace AsyncLinq;

public static partial class AsyncEnumerableExtensions {
    public static IAsyncEnumerable<TSource> AsAsyncEnumerable<TSource>(
        this IObservable<TSource> source, 
        int maxBuffer = -1) {

        if (source == null) {
            throw new ArgumentNullException(nameof(source));
        }

        return new ObservableWrapper<TSource>(default, source, maxBuffer);
    }

    private class ObservableWrapper<T> : IAsyncOperator<T> {
        private readonly IObservable<T> source;
        private readonly int maxBuffer;
        
        public AsyncOperatorParams Params { get; }

        public ObservableWrapper(AsyncOperatorParams pars, IObservable<T> source, int maxBuffer) {
            this.Params = pars;
            this.source = source;
            this.maxBuffer = maxBuffer;
        }
        
        public IAsyncOperator<T> WithParams(AsyncOperatorParams pars) {
            return new ObservableWrapper<T>(pars, this.source, this.maxBuffer);
        }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
            Channel<T> channel;

            if (maxBuffer <= 0) {
                channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions() {
                    AllowSynchronousContinuations = true
                });
            }
            else {
                channel = Channel.CreateBounded<T>(new BoundedChannelOptions(maxBuffer) {
                    AllowSynchronousContinuations = true,
                    FullMode = BoundedChannelFullMode.DropWrite
                });
            }

            var channelObserver = new ChannelObserver<T>(channel);
            var sub = this.source.Subscribe(channelObserver);

            return new Enumerator(channel, sub, cancellationToken);
        }

        private class Enumerator : IAsyncEnumerator<T> {
            private readonly Channel<T> channel;
            private readonly IDisposable channelSub;
            private readonly CancellationToken cancellationToken;

            public Enumerator(Channel<T> channel, IDisposable channelSub, CancellationToken cancellationToken) {
                this.channel = channel;
                this.channelSub = channelSub;
                this.cancellationToken = cancellationToken;
            }

            public T Current { get; private set; } = default!;

            public ValueTask DisposeAsync() {
                this.channelSub.Dispose();

                return default;
            }

            public async ValueTask<bool> MoveNextAsync() {
                var hasMore = await this.channel.Reader.WaitToReadAsync(this.cancellationToken);

                if (!hasMore) {
                    return false;
                }

                if (!this.channel.Reader.TryRead(out var item)) {
                    return false;
                }

                this.Current = item;
                return true;
            }
        }
    }

    private class ChannelObserver<T> : IObserver<T> {
        private readonly Channel<T> channel;

        public ChannelObserver(Channel<T> channel) {
            this.channel = channel;
        }

        public void OnCompleted() {
            this.channel.Writer.Complete();
        }

        public void OnError(Exception error) {
            this.channel.Writer.Complete();
        }

        public void OnNext(T value) {
            this.channel.Writer.TryWrite(value);
        }
    }    
}