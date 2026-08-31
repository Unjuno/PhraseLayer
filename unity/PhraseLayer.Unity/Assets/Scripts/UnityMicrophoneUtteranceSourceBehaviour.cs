using System;
using System.Collections.Generic;
using PhraseLayer.Core.Inputs;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Unity microphone adapter that turns the looping Microphone AudioClip into complete mono AudioChunk work
    /// items for LiveListenModeCoordinator. Segmentation is intentionally a small replaceable energy/silence gate:
    /// no VAD policy leaks into Core and a learned VAD can replace this component later.
    ///
    /// On Android/Quest, using UnityEngine.Microphone causes Unity to include RECORD_AUDIO and request the runtime
    /// permission on first microphone use. This component still fails visibly when no device/capture is available.
    /// </summary>
    public sealed class UnityMicrophoneUtteranceSourceBehaviour : MonoBehaviour
    {
        [SerializeField] private string microphoneDevice = string.Empty;
        [SerializeField] private int requestedSampleRate = 48000;
        [SerializeField] private int ringBufferSeconds = 12;
        [SerializeField] private float activationRms = 0.015f;
        [SerializeField] private float releaseRms = 0.008f;
        [SerializeField] private float minimumUtteranceSeconds = 0.25f;
        [SerializeField] private float silenceToFinalizeSeconds = 0.60f;
        [SerializeField] private float maximumUtteranceSeconds = 8.0f;
        [SerializeField] private bool startOnEnable = true;
        [SerializeField] private string lastStatus = "Microphone capture not started.";

        private readonly List<float> utterance = new List<float>();
        private AudioClip microphoneClip;
        private string activeDevice;
        private int lastReadPosition;
        private bool hasReadPosition;
        private bool speechActive;
        private double lastSpeechRealtime;
        private long lastEmittedTimestampMicroseconds = -1;

        public event Action<AudioChunk> UtteranceReady;

        public bool IsCapturing => microphoneClip != null && Microphone.IsRecording(activeDevice);
        public bool IsSpeechActive => speechActive;
        public string LastStatus => lastStatus;
        public int CaptureSampleRate => microphoneClip != null ? microphoneClip.frequency : 0;

        private void OnEnable()
        {
            if (startOnEnable)
            {
                try
                {
                    StartCapture();
                }
                catch (Exception exception)
                {
                    lastStatus = exception.GetType().Name + ": " + exception.Message;
                    Debug.LogException(exception, this);
                    enabled = false;
                }
            }
        }

        public void StartCapture()
        {
            if (IsCapturing)
                return;
            ValidateSettings();

            var devices = Microphone.devices;
            if (devices == null || devices.Length == 0)
                throw new InvalidOperationException("No Unity microphone device is available.");

            activeDevice = string.IsNullOrWhiteSpace(microphoneDevice) ? devices[0] : microphoneDevice;
            microphoneClip = Microphone.Start(activeDevice, true, ringBufferSeconds, requestedSampleRate);
            if (microphoneClip == null)
                throw new InvalidOperationException("Unity Microphone.Start returned no AudioClip for device '" + activeDevice + "'.");

            lastReadPosition = 0;
            hasReadPosition = false;
            speechActive = false;
            utterance.Clear();
            lastStatus = string.Format(
                "Microphone capture started: {0}; requested={1} Hz; actual={2} Hz; energy-gated utterances.",
                activeDevice,
                requestedSampleRate,
                microphoneClip.frequency);
            Debug.Log(lastStatus, this);
        }

        public void StopCapture(bool flushActiveUtterance = false)
        {
            if (flushActiveUtterance)
                FlushActiveUtterance();
            else
                ResetUtterance();

            if (!string.IsNullOrEmpty(activeDevice) && Microphone.IsRecording(activeDevice))
                Microphone.End(activeDevice);
            if (microphoneClip != null)
                Destroy(microphoneClip);
            microphoneClip = null;
            activeDevice = null;
            hasReadPosition = false;
            lastReadPosition = 0;
            lastStatus = "Microphone capture stopped.";
        }

        private void OnDisable()
        {
            StopCapture(flushActiveUtterance: false);
        }

        private void OnDestroy()
        {
            StopCapture(flushActiveUtterance: false);
        }

        private void Update()
        {
            if (microphoneClip == null || string.IsNullOrEmpty(activeDevice) || !Microphone.IsRecording(activeDevice))
                return;

            var currentPosition = Microphone.GetPosition(activeDevice);
            if (currentPosition < 0 || currentPosition > microphoneClip.samples)
            {
                lastStatus = "Microphone returned an invalid recording position.";
                return;
            }
            if (!hasReadPosition)
            {
                if (currentPosition == 0)
                    return;
                lastReadPosition = currentPosition;
                hasReadPosition = true;
                return;
            }
            if (currentPosition == lastReadPosition)
                return;

            if (currentPosition > lastReadPosition)
            {
                ReadAndProcess(lastReadPosition, currentPosition - lastReadPosition);
            }
            else
            {
                ReadAndProcess(lastReadPosition, microphoneClip.samples - lastReadPosition);
                if (currentPosition > 0)
                    ReadAndProcess(0, currentPosition);
            }
            lastReadPosition = currentPosition;
        }

        private void ReadAndProcess(int frameOffset, int frameCount)
        {
            if (frameCount <= 0 || microphoneClip == null)
                return;
            var channels = microphoneClip.channels;
            if (channels <= 0)
                throw new InvalidOperationException("Microphone AudioClip has no channels.");

            var interleaved = new float[checked(frameCount * channels)];
            if (!microphoneClip.GetData(interleaved, frameOffset))
                throw new InvalidOperationException("Failed to read Unity microphone ring buffer.");

            var mono = new float[frameCount];
            double squared = 0.0;
            for (var frame = 0; frame < frameCount; frame++)
            {
                double sum = 0.0;
                var baseIndex = frame * channels;
                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = interleaved[baseIndex + channel];
                    if (float.IsNaN(sample) || float.IsInfinity(sample))
                        throw new InvalidOperationException("Unity microphone returned a non-finite audio sample.");
                    sum += sample;
                }
                var value = (float)(sum / channels);
                if (value > 1f) value = 1f;
                if (value < -1f) value = -1f;
                mono[frame] = value;
                squared += value * (double)value;
            }

            var rms = mono.Length == 0 ? 0.0 : Math.Sqrt(squared / mono.Length);
            var now = Time.realtimeSinceStartupAsDouble;
            if (!speechActive)
            {
                if (rms < activationRms)
                    return;
                speechActive = true;
                lastSpeechRealtime = now;
                utterance.Clear();
            }

            utterance.AddRange(mono);
            if (rms >= releaseRms)
                lastSpeechRealtime = now;

            var duration = utterance.Count / (double)microphoneClip.frequency;
            if (duration >= maximumUtteranceSeconds ||
                (duration >= minimumUtteranceSeconds && now - lastSpeechRealtime >= silenceToFinalizeSeconds))
            {
                EmitUtterance();
            }
        }

        public void FlushActiveUtterance()
        {
            if (!speechActive || microphoneClip == null)
                return;
            var duration = utterance.Count / (double)microphoneClip.frequency;
            if (duration >= minimumUtteranceSeconds)
                EmitUtterance();
            else
                ResetUtterance();
        }

        private void EmitUtterance()
        {
            if (microphoneClip == null || utterance.Count == 0)
            {
                ResetUtterance();
                return;
            }

            var samples = utterance.ToArray();
            var timestamp = checked((long)Math.Round(Time.realtimeSinceStartupAsDouble * 1000000.0));
            if (timestamp <= lastEmittedTimestampMicroseconds)
                timestamp = checked(lastEmittedTimestampMicroseconds + 1);
            lastEmittedTimestampMicroseconds = timestamp;

            ResetUtterance();
            var chunk = new AudioChunk(samples, microphoneClip.frequency, timestamp);
            lastStatus = string.Format(
                "Captured utterance: {0:F2}s at {1} Hz.",
                samples.Length / (double)microphoneClip.frequency,
                microphoneClip.frequency);
            UtteranceReady?.Invoke(chunk);
        }

        private void ResetUtterance()
        {
            utterance.Clear();
            speechActive = false;
            lastSpeechRealtime = 0.0;
        }

        private void ValidateSettings()
        {
            if (requestedSampleRate <= 0)
                throw new InvalidOperationException("Requested microphone sample rate must be positive.");
            if (ringBufferSeconds < 2)
                throw new InvalidOperationException("Microphone ring buffer must be at least two seconds.");
            if (!(activationRms > 0f && activationRms <= 1f))
                throw new InvalidOperationException("Microphone activation RMS must be in (0,1].");
            if (!(releaseRms > 0f && releaseRms <= activationRms))
                throw new InvalidOperationException("Microphone release RMS must be in (0, activation RMS].");
            if (!(minimumUtteranceSeconds > 0f))
                throw new InvalidOperationException("Minimum utterance duration must be positive.");
            if (!(silenceToFinalizeSeconds > 0f))
                throw new InvalidOperationException("Silence finalization duration must be positive.");
            if (!(maximumUtteranceSeconds >= minimumUtteranceSeconds))
                throw new InvalidOperationException("Maximum utterance duration must be >= minimum duration.");
        }
    }
}
