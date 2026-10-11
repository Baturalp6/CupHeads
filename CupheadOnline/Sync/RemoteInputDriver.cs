using System.Collections.Generic;
using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Stores the most recently received gameplay input for network-controlled
    /// slots. Today that is still one live remote slot, but the storage is keyed
    /// by PlayerId so the rest of the code does not have to stay single-remote.
    /// </summary>
    public static class RemoteInputDriver
    {
        sealed class RemoteInputState
        {
            public InputFramePacket Current;
            public InputFramePacket Previous;
            public bool HasData;
            public bool HasReceivedTick;
            public uint LastReceivedTick;
            public int StallFrames;
            public uint DownEdges;
            public uint UpEdges;
            public readonly int[] DownServedFrames = CreateServedFrameBuffer();
            public readonly int[] UpServedFrames = CreateServedFrameBuffer();
            public readonly float[] DownMarkedAt = new float[32];
            public readonly float[] UpMarkedAt = new float[32];
        }

        const int MAX_STALL = 45; // ~750 ms at 60 Hz before inputs are released
        // An edge nobody queried within this window (e.g. jump pressed while
        // the player was input-locked) is dropped instead of firing later.
        const float EDGE_LIFETIME = 0.25f;

        static readonly Dictionary<byte, RemoteInputState> _states =
            new Dictionary<byte, RemoteInputState>(2);

        static RemoteInputDriver()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static void Apply(InputFramePacket pkt)
        {
            Apply(MultiplayerSession.GetPrimaryRemoteGameplayId(), pkt);
        }

        public static void Apply(byte participantId, InputFramePacket pkt)
        {
            var state = GetOrCreateState(participantId);

            if (state.HasReceivedTick)
            {
                if (pkt.Tick == state.LastReceivedTick)
                    return;

                if (!NetTick.IsNewer(pkt.Tick, state.LastReceivedTick))
                    return;
            }

            if (state.HasData)
                state.Previous = state.Current;
            else
                state.Previous = default(InputFramePacket);

            state.Current = pkt;
            state.HasData = true;
            state.HasReceivedTick = true;
            state.LastReceivedTick = pkt.Tick;
            state.StallFrames = 0;
            AccumulateEdges(state);
        }

        public static void Apply(PlayerId playerId, InputFramePacket pkt)
        {
            Apply((byte)playerId, pkt);
        }

        public static bool HasDataFor(PlayerId playerId)
        {
            RemoteInputState state;
            return _states.TryGetValue((byte)playerId, out state) && state.HasData;
        }

        public static bool TryGetCurrent(PlayerId playerId, out InputFramePacket pkt)
        {
            return TryGetCurrent((byte)playerId, out pkt);
        }

        public static bool TryGetCurrent(byte participantId, out InputFramePacket pkt)
        {
            RemoteInputState state;
            if (_states.TryGetValue(participantId, out state) && state.HasData)
            {
                pkt = state.Current;
                return true;
            }

            pkt = default(InputFramePacket);
            return false;
        }

        public static bool WasPressedThisFrame(byte participantId, CupheadButton button)
        {
            RemoteInputState state;
            if (!_states.TryGetValue(participantId, out state) || !state.HasData)
                return false;

            return ConsumeEdgeForFrame(ref state.DownEdges, state.DownServedFrames, state.DownMarkedAt, button);
        }

        public static bool WasReleasedThisFrame(byte participantId, CupheadButton button)
        {
            RemoteInputState state;
            if (!_states.TryGetValue(participantId, out state) || !state.HasData)
                return false;

            return ConsumeEdgeForFrame(ref state.UpEdges, state.UpServedFrames, state.UpMarkedAt, button);
        }

        public static bool IsPressed(byte participantId, CupheadButton button)
        {
            RemoteInputState state;
            return _states.TryGetValue(participantId, out state)
                && state.HasData
                && state.Current.IsPressed(button);
        }

        /// <summary>Called each FixedUpdate by PlayerMotorPatch to age the input.</summary>
        public static void Tick(PlayerId playerId)
        {
            Tick((byte)playerId);
        }

        public static void Tick(byte participantId)
        {
            RemoteInputState state;
            if (!_states.TryGetValue(participantId, out state) || !state.HasData)
                return;

            state.StallFrames++;
            if (state.StallFrames > MAX_STALL)
            {
                // Starvation: zero all inputs so the proxy player stops moving.
                state.Previous = default(InputFramePacket);
                state.Current = default(InputFramePacket);
                state.HasData = false;
                state.DownEdges = 0u;
                state.UpEdges = 0u;
                ResetServedFrames(state.DownServedFrames);
                ResetServedFrames(state.UpServedFrames);
            }
        }

        public static void Reset()
        {
            _states.Clear();
        }

        public static void Reset(PlayerId playerId)
        {
            _states.Remove((byte)playerId);
        }

        public static void Reset(byte participantId)
        {
            _states.Remove(participantId);
        }

        static RemoteInputState GetOrCreateState(PlayerId playerId)
        {
            return GetOrCreateState((byte)playerId);
        }

        static RemoteInputState GetOrCreateState(byte participantId)
        {
            RemoteInputState state;
            if (!_states.TryGetValue(participantId, out state))
            {
                state = new RemoteInputState();
                _states[participantId] = state;
            }
            return state;
        }

        /// <summary>
        /// Adds this packet's press/release edges to the pending set instead of
        /// replacing it. Several packets can arrive between two game queries
        /// (jitter, frame-rate differences); overwriting the edge mask used to
        /// erase presses the game had not read yet. PressLatch additionally
        /// recovers taps whose own frames were dropped in transit.
        /// </summary>
        static void AccumulateEdges(RemoteInputState state)
        {
            uint down = (state.Current.Buttons & ~state.Previous.Buttons)
                      | (state.Current.PressLatch & ~state.Previous.PressLatch);
            uint up = state.Previous.Buttons & ~state.Current.Buttons;

            MarkNewEdges(ref state.DownEdges, state.DownServedFrames, state.DownMarkedAt, down);
            MarkNewEdges(ref state.UpEdges, state.UpServedFrames, state.UpMarkedAt, up);
        }

        static void MarkNewEdges(ref uint pending, int[] servedFrames, float[] markedAt, uint newEdges)
        {
            if (newEdges == 0u)
                return;

            float now = Time.unscaledTime;
            pending |= newEdges;
            for (int i = 0; i < 32; i++)
            {
                if ((newEdges & (1u << i)) != 0u)
                {
                    servedFrames[i] = -1;
                    markedAt[i] = now;
                }
            }
        }

        /// <summary>
        /// A pending edge reads as true for every query during the first frame
        /// the game asks for it, then expires.
        /// </summary>
        static bool ConsumeEdgeForFrame(ref uint pending, int[] servedFrames, float[] markedAt, CupheadButton button)
        {
            int buttonIndex = (int)button;
            if (buttonIndex < 0 || buttonIndex >= 32)
                return false;

            uint mask = 1u << buttonIndex;
            int frame = Time.frameCount;
            if (servedFrames[buttonIndex] == frame)
                return true;
            if ((pending & mask) == 0u)
                return false;

            if (servedFrames[buttonIndex] >= 0
             || Time.unscaledTime - markedAt[buttonIndex] > EDGE_LIFETIME)
            {
                // Served in an earlier frame and no newer edge since: expire it.
                pending &= ~mask;
                servedFrames[buttonIndex] = -1;
                return false;
            }

            servedFrames[buttonIndex] = frame;
            return true;
        }

        static int[] CreateServedFrameBuffer()
        {
            var frames = new int[32];
            ResetServedFrames(frames);
            return frames;
        }

        static void ResetServedFrames(int[] frames)
        {
            if (frames == null)
                return;

            for (int i = 0; i < frames.Length; i++)
                frames[i] = -1;
        }
    }
}
