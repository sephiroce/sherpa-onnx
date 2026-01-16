using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SherpaOnnxASR
{
    public class AudioInputDevice
    {
        public int Index { get; set; }  // -1 for System Audio, 0+ for mic devices
        public string Name { get; set; } = "";
        public bool IsSystemAudio => Index == -1;
        
        public override string ToString() => Name;
    }

    public class AudioService : IDisposable
    {
        private IWaveIn? _waveIn;
        private BufferedWaveProvider? _bufferedWave;
        private ISampleProvider? _sampleProvider;
        
        private Thread? _processThread;
        private volatile bool _isCapturing;

        private const int TARGET_SAMPLE_RATE = 16000;

        public event Action<string>? OnLog;
        public event Action<float[]>? OnAudioAvailable; 
        public event Action<double>? OnAudioProcessed;
        public event Action<bool>? OnVadStatus;
        public event Action<double>? OnLevelChanged;  // Audio level in dB (-60 to 0)

        // Selected device index (-1 = System Audio, 0+ = mic)
        public int SelectedDeviceIndex { get; private set; } = -1;
        
        // Microphone gain (amplification factor for quiet mics) - read from AppSettings
        public float MicGain => AppSettings.MicGain;
        
        // VAD Settings
        private const float VAD_THRESHOLD = 0.005f; 
        private const double VAD_HANGOVER_SEC = 1.0;
        private DateTime _lastSpeechTime = DateTime.MinValue;
        private bool _wasSpeaking = false;
        
        // Level meter throttling (max 10 updates/sec)
        private DateTime _lastLevelUpdate = DateTime.MinValue;
        private const double LEVEL_UPDATE_INTERVAL_MS = 100;
        
        // Ring buffer for 1 second of audio
        private const int RING_BUFFER_SEC = 1;
        private readonly List<float> _ringBuffer = new List<float>();
        private readonly int _ringBufferMaxSize = TARGET_SAMPLE_RATE * RING_BUFFER_SEC;
        private bool _contextSent = false;

        /// <summary>
        /// Get list of available audio input devices (System Audio + microphones)
        /// </summary>
        public static List<AudioInputDevice> GetAudioInputDevices()
        {
            var list = new List<AudioInputDevice>
            {
                new AudioInputDevice { Index = -1, Name = "🔊 System Audio" }
            };
            
            try
            {
                for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                {
                    var caps = WaveInEvent.GetCapabilities(i);
                    list.Add(new AudioInputDevice 
                    { 
                        Index = i, 
                        Name = $"🎤 {caps.ProductName}" 
                    });
                }
            }
            catch { }
            
            return list;
        }

        /// <summary>
        /// Set audio input device (only when not capturing)
        /// </summary>
        public bool SetDevice(int deviceIndex)
        {
            if (_isCapturing)
            {
                OnLog?.Invoke("Cannot change input while capturing");
                return false;
            }

            SelectedDeviceIndex = deviceIndex;
            string name = deviceIndex == -1 ? "System Audio" : $"Mic {deviceIndex}";
            OnLog?.Invoke($"Audio input set to: {name}");
            return true;
        }

        public void StartCapture()
        {
            if (_isCapturing) return;

            try
            {
                if (SelectedDeviceIndex >= 0)
                {
                    // [FIX] 마이크 입력 설정
                    var waveIn = new WaveInEvent();
                    waveIn.DeviceNumber = SelectedDeviceIndex;
                    
                    // 핵심 수정: 고품질 포맷 강제 지정 (44.1kHz, 16bit, Mono)
                    // 이렇게 해야 8kHz(전화기 음질)로 잡히는 문제를 막을 수 있습니다.
                    waveIn.WaveFormat = new WaveFormat(44100, 16, 1); 

                    waveIn.BufferMilliseconds = 100;
                    _waveIn = waveIn;

                    _bufferedWave = new BufferedWaveProvider(_waveIn.WaveFormat);
                    _bufferedWave.BufferDuration = TimeSpan.FromSeconds(5);
                    _bufferedWave.DiscardOnBufferOverflow = true;

                    // 파이프라인: Buffered -> Sample -> Resample (필요시)
                    ISampleProvider source = _bufferedWave.ToSampleProvider();

                    // 44.1kHz -> 16kHz 리샘플링
                    if (source.WaveFormat.SampleRate != TARGET_SAMPLE_RATE)
                    {
                        source = new WdlResamplingSampleProvider(source, TARGET_SAMPLE_RATE);
                    }

                    // 위에서 Mono(1채널)로 강제했으므로, 
                    // 스테레오 믹싱 로직(소리 작아짐/위상 문제 원인)은 자연스럽게 건너뛰게 됩니다.
                    if (source.WaveFormat.Channels > 1)
                    {
                        source = new StereoToMonoSampleProvider(source)
                        {
                            LeftVolume = 0.5f,
                            RightVolume = 0.5f
                        };
                    }

                    _sampleProvider = source;
                    OnLog?.Invoke($"Using Microphone #{SelectedDeviceIndex} (Forced 44.1kHz -> 16kHz)");
                }
                else
                {
                    // 시스템 오디오 (루프백) - 기존과 동일
                    var loopback = new WasapiLoopbackCapture();
                    _waveIn = loopback;
                    _bufferedWave = new BufferedWaveProvider(_waveIn.WaveFormat);
                    _bufferedWave.BufferDuration = TimeSpan.FromSeconds(5);
                    _bufferedWave.DiscardOnBufferOverflow = true;

                    ISampleProvider source = _bufferedWave.ToSampleProvider();
                    
                    if (source.WaveFormat.SampleRate != TARGET_SAMPLE_RATE)
                    {
                        source = new WdlResamplingSampleProvider(source, TARGET_SAMPLE_RATE);
                    }
                    
                    if (source.WaveFormat.Channels > 1)
                    {
                        source = new StereoToMonoSampleProvider(source)
                        {
                            LeftVolume = 0.5f,
                            RightVolume = 0.5f
                        };
                    }
                    
                    _sampleProvider = source;
                    OnLog?.Invoke("Using System Audio (loopback)");
                }

                // 공통 실행 로직
                _waveIn.DataAvailable += (s, e) => _bufferedWave?.AddSamples(e.Buffer, 0, e.BytesRecorded);
                _waveIn.RecordingStopped += (s, e) => OnLog?.Invoke("Capture Stopped.");

                _ringBuffer.Clear();
                _contextSent = false;
                _wasSpeaking = false;
                _lastSpeechTime = DateTime.MinValue;
                
                _isCapturing = true;
                _waveIn.StartRecording();

                _processThread = new Thread(ProcessLoop);
                _processThread.IsBackground = true;
                _processThread.Start();
                
                OnLog?.Invoke("Capturing started.");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"Error starting: {ex.Message}");
                StopCapture();
            }
        }

        private void ProcessLoop()
        {
            int bufferSize = (int)(TARGET_SAMPLE_RATE * 0.1);
            float[] buffer = new float[bufferSize];
            
            // Use higher threshold for mic input
            float vadThreshold = SelectedDeviceIndex >= 0 ? 0.01f : VAD_THRESHOLD;

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
                        
                        // Apply mic gain (only for microphone input)
                        if (SelectedDeviceIndex >= 0 && MicGain != 1.0f)
                        {
                            for (int i = 0; i < samplesRead; i++)
                            {
                                validSamples[i] *= MicGain;
                                // Clip to prevent distortion
                                if (validSamples[i] > 1.0f) validSamples[i] = 1.0f;
                                else if (validSamples[i] < -1.0f) validSamples[i] = -1.0f;
                            }
                        }

                        float sumSquares = 0;
                        for (int i = 0; i < samplesRead; i++) 
                            sumSquares += validSamples[i] * validSamples[i];
                        double rms = Math.Sqrt(sumSquares / samplesRead);
                        
                        // Report level (throttled to ~10 updates/sec)
                        if ((DateTime.Now - _lastLevelUpdate).TotalMilliseconds >= LEVEL_UPDATE_INTERVAL_MS)
                        {
                            // Convert RMS to dB (clamp to -60 to 0 range)
                            double db = rms > 0.000001 ? 20 * Math.Log10(rms) : -60;
                            db = Math.Max(-60, Math.Min(0, db));
                            OnLevelChanged?.Invoke(db);
                            _lastLevelUpdate = DateTime.Now;
                        }

                        bool hasVoice = rms > vadThreshold;
                        if (hasVoice)
                        {
                            _lastSpeechTime = DateTime.Now;
                        }
                        
                        bool isSpeaking = hasVoice || 
                            (_lastSpeechTime != DateTime.MinValue && 
                             (DateTime.Now - _lastSpeechTime).TotalSeconds < VAD_HANGOVER_SEC);

                        if (isSpeaking != _wasSpeaking)
                        {
                            _wasSpeaking = isSpeaking;
                            OnVadStatus?.Invoke(isSpeaking);
                        }

                        if (isSpeaking)
                        {
                            if (!_contextSent && _ringBuffer.Count > 0)
                            {
                                float[] contextAudio = _ringBuffer.ToArray();
                                OnAudioAvailable?.Invoke(contextAudio);
                                OnAudioProcessed?.Invoke(contextAudio.Length / (double)TARGET_SAMPLE_RATE);
                                _ringBuffer.Clear();
                                _contextSent = true;
                            }
                            
                            OnAudioAvailable?.Invoke(validSamples);
                            OnAudioProcessed?.Invoke(samplesRead / (double)TARGET_SAMPLE_RATE);
                        }
                        else
                        {
                            _ringBuffer.AddRange(validSamples);
                            
                            if (_ringBuffer.Count > _ringBufferMaxSize)
                            {
                                _ringBuffer.RemoveRange(0, _ringBuffer.Count - _ringBufferMaxSize);
                            }
                            
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
            try { _waveIn?.StopRecording(); } catch { }
            Thread.Sleep(50); 
            try { _waveIn?.Dispose(); } catch { }
            _waveIn = null;
            _sampleProvider = null;
            _bufferedWave = null;
        }
        
        public void Dispose()
        {
            StopCapture();
        }
    }
}