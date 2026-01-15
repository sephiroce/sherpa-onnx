using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.Threading;

namespace MoshiLiveCaption
{
    public class AudioService : IDisposable
    {
        private WasapiLoopbackCapture? _capture;
        private BufferedWaveProvider? _bufferedWave;
        private ISampleProvider? _sampleProvider;
        
        private Thread? _processThread;
        private volatile bool _isCapturing;

        // SherpaOnnx 표준: 16kHz
        private const int TARGET_SAMPLE_RATE = 16000;

        public event Action<string>? OnLog;
        public event Action<float[]>? OnAudioAvailable; 
        public event Action<double>? OnAudioProcessed;
        public event Action<bool>? OnVadStatus;

        // VAD Settings
        private const float VAD_THRESHOLD = 0.005f; 
        private const double VAD_HANGOVER_SEC = 1.0;  // Hangover를 줄임 (trailing audio)
        private DateTime _lastSpeechTime = DateTime.MinValue;
        private bool _wasSpeaking = false;
        
        // Ring buffer for 1 second of audio (to prepend context when speech starts)
        private const int RING_BUFFER_SEC = 1;
        private readonly List<float> _ringBuffer = new List<float>();
        private readonly int _ringBufferMaxSize = TARGET_SAMPLE_RATE * RING_BUFFER_SEC;
        private bool _contextSent = false;  // true after we send the pre-speech context

        public void StartCapture()
        {
            if (_isCapturing) return;

            try
            {
                _capture = new WasapiLoopbackCapture();
                
                _bufferedWave = new BufferedWaveProvider(_capture.WaveFormat);
                _bufferedWave.BufferDuration = TimeSpan.FromSeconds(5);
                _bufferedWave.DiscardOnBufferOverflow = true;

                // NAudio 파이프라인: Raw -> Float -> Resample(16k) -> Mono
                ISampleProvider source = _bufferedWave.ToSampleProvider();
                
                if (source.WaveFormat.SampleRate != TARGET_SAMPLE_RATE)
                {
                    source = new WdlResamplingSampleProvider(source, TARGET_SAMPLE_RATE);
                }
                
                if (source.WaveFormat.Channels > 1)
                {
                    source = new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f };
                }

                _sampleProvider = source;

                _capture.DataAvailable += (s, e) => _bufferedWave.AddSamples(e.Buffer, 0, e.BytesRecorded);
                _capture.RecordingStopped += (s, e) => OnLog?.Invoke("Capture Stopped.");

                // Reset state
                _ringBuffer.Clear();
                _contextSent = false;
                _wasSpeaking = false;
                _lastSpeechTime = DateTime.MinValue;
                
                _isCapturing = true;
                _capture.StartRecording();

                _processThread = new Thread(ProcessLoop);
                _processThread.IsBackground = true;
                _processThread.Start();
                
                OnLog?.Invoke($"Capturing started. Resampling to 16kHz.");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"Error starting: {ex.Message}");
                StopCapture();
            }
        }

        private void ProcessLoop()
        {
            int bufferSize = (int)(TARGET_SAMPLE_RATE * 0.1); // 0.1초 버퍼
            float[] buffer = new float[bufferSize];

            while (_isCapturing && _sampleProvider != null)
            {
                try
                {
                    if (_bufferedWave == null || _bufferedWave.BufferedDuration.TotalMilliseconds < 100)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    int samplesRead = _sampleProvider.Read(buffer, 0, buffer.Length);
                    
                    if (!_isCapturing) break;

                    if (samplesRead > 0)
                    {
                        float[] validSamples = new float[samplesRead];
                        Array.Copy(buffer, validSamples, samplesRead);

                        // VAD Check (RMS)
                        float sumSquares = 0;
                        for (int i = 0; i < samplesRead; i++) 
                            sumSquares += validSamples[i] * validSamples[i];
                        double rms = Math.Sqrt(sumSquares / samplesRead);

                        // Detect if currently speaking
                        bool hasVoice = rms > VAD_THRESHOLD;
                        if (hasVoice)
                        {
                            _lastSpeechTime = DateTime.Now;
                        }
                        
                        // Speaking = voice detected OR within hangover period
                        bool isSpeaking = hasVoice || 
                            (_lastSpeechTime != DateTime.MinValue && 
                             (DateTime.Now - _lastSpeechTime).TotalSeconds < VAD_HANGOVER_SEC);

                        // Notify VAD status change
                        if (isSpeaking != _wasSpeaking)
                        {
                            _wasSpeaking = isSpeaking;
                            OnVadStatus?.Invoke(isSpeaking);
                        }

                        if (isSpeaking)
                        {
                            // --- SPEECH DETECTED ---
                            
                            // First, send the 1-second context buffer (only once per speech segment)
                            if (!_contextSent && _ringBuffer.Count > 0)
                            {
                                float[] contextAudio = _ringBuffer.ToArray();
                                OnAudioAvailable?.Invoke(contextAudio);
                                OnAudioProcessed?.Invoke(contextAudio.Length / (double)TARGET_SAMPLE_RATE);
                                _ringBuffer.Clear();
                                _contextSent = true;
                                OnLog?.Invoke($"Sent {contextAudio.Length / (double)TARGET_SAMPLE_RATE:F2}s context buffer");
                            }
                            
                            // Send current audio chunk
                            OnAudioAvailable?.Invoke(validSamples);
                            OnAudioProcessed?.Invoke(samplesRead / (double)TARGET_SAMPLE_RATE);
                        }
                        else
                        {
                            // --- SILENCE ---
                            
                            // Add current audio to ring buffer (keep last 1 sec)
                            _ringBuffer.AddRange(validSamples);
                            
                            // Trim to max size
                            if (_ringBuffer.Count > _ringBufferMaxSize)
                            {
                                _ringBuffer.RemoveRange(0, _ringBuffer.Count - _ringBufferMaxSize);
                            }
                            
                            // Reset context flag so next speech will send buffer
                            _contextSent = false;
                        }
                    }
                }
                catch { break; }
            }
        }

        public void StopCapture()
        {
            _isCapturing = false;
            try { _capture?.StopRecording(); } catch { }
            Thread.Sleep(50); 
            try { _capture?.Dispose(); } catch { }
            _sampleProvider = null;
            _bufferedWave = null;
        }
        
        public void Dispose()
        {
            StopCapture();
        }
    }
}