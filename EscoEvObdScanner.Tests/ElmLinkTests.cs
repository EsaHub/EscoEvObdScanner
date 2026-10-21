using System.Collections.Concurrent;
using System.Text;

namespace EscoEvObdScanner.Tests
{
    /// <summary>
    /// The adapter protocol over a channel that delivers a reply in pieces of a given size,
    /// the prompt anywhere in them: the reply ends at the prompt, a lost channel is a lost
    /// port, and a busy adapter is interrupted.
    /// </summary>
    public class ElmLinkTests
    {
        /// <summary>
        /// A channel that answers each command with a scripted reply, split into pieces of
        /// <see cref="Piece"/> bytes, each piece arriving <see cref="GapMs"/> after the last.
        /// </summary>
        sealed class PiecesChannel : IElmChannel
        {
            readonly Queue<string> _replies;
            readonly BlockingCollection<int> _in = new();
            public readonly List<string> Written = new();
            public int Piece = 20;
            public int GapMs;
            public bool Lost;

            public PiecesChannel(params string[] replies) => _replies = new Queue<string>(replies);

            public string Name => "BLE test";
            public bool IsOpen { get; private set; }
            public void Open() => IsOpen = true;

            public void Write(string text)
            {
                if (Lost) throw new IOException("link dropped");
                Written.Add(text);
                if (text == " " || _replies.Count == 0) return;
                var reply = Encoding.ASCII.GetBytes(_replies.Dequeue());
                // the pieces arrive on another thread while the protocol reads
                Task.Run(async () =>
                {
                    for (int i = 0; i < reply.Length; i += Piece)
                    {
                        if (GapMs > 0) await Task.Delay(GapMs);
                        foreach (var b in reply.Skip(i).Take(Piece)) _in.Add(b);
                    }
                });
            }

            public int Read(int timeoutMs)
            {
                if (Lost) throw new IOException("link dropped");
                return _in.TryTake(out int b, timeoutMs) ? b : -1;
            }

            public void DiscardInput() { while (_in.TryTake(out _)) { } }

            public void Dispose() => IsOpen = false;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(20)]
        [InlineData(244)]
        public void ReplyInPiecesIsReadToThePrompt(int piece)
        {
            // the e-CMP cell voltages: a multi-frame reply of several lines
            string reply = "0F3\r0:62D440000F1C\r1:0F1D0F1C0F1B0F1C\r2:0F1D0F1C0F1D0F1E\r\r>";
            var channel = new PiecesChannel(reply) { Piece = piece };
            using var link = new ElmLink(channel);
            link.Open();

            var lines = link.Send("22D440");

            Assert.Equal(new[] { "0F3", "0:62D440000F1C", "1:0F1D0F1C0F1B0F1C", "2:0F1D0F1C0F1D0F1E" }, lines);
            Assert.Equal("22D440\r", channel.Written[0]);
        }

        [Fact]
        public void PromptAloneInTheLastPieceEndsTheReply()
        {
            // "62D4108996\r" fills the first piece exactly; the prompt comes alone in the next
            var channel = new PiecesChannel("62D4108996\r>") { Piece = 11, GapMs = 30 };
            using var link = new ElmLink(channel);
            link.Open();

            Assert.Equal(new[] { "62D4108996" }, link.Send("22D410"));
        }

        sealed class DetailsChannel : IElmChannel
        {
            public string Name => "BLE test";
            public bool IsOpen => true;
            public string? Details => "service fff0";
            public void Open() { }
            public void Write(string text) { }
            public int Read(int timeoutMs) => -1;
            public void DiscardInput() { }
            public void Dispose() { }
        }

        [Fact]
        public void WhatTheChannelFoundOnOpeningGoesToTheTrace()
        {
            using var link = new ElmLink(new DetailsChannel());
            var trace = new List<string>();
            link.Trace += trace.Add;
            link.Open();
            Assert.Equal(new[] { "[BLE test] service fff0" }, trace);
        }

        [Fact]
        public void LostChannelIsALostPort()
        {
            var channel = new PiecesChannel();
            using var link = new ElmLink(channel);
            link.Open();
            channel.Lost = true;

            var ex = Assert.Throws<PortLostException>(() => link.Send("22D410"));
            Assert.Equal("BLE test", ex.Port);
        }

        [Fact]
        public void AdapterThatNeverPromptsIsInterruptedThenGivenUp()
        {
            // no reply at all: the protocol sends a space to stop a busy adapter, then gives up
            var channel = new PiecesChannel();
            using var link = new ElmLink(channel);
            link.Open();

            Assert.Throws<PortLostException>(() => link.Send("22D410", timeoutMs: 200));
            Assert.Equal(new[] { "22D410\r", " " }, channel.Written);
        }
    }
}
