using System;
using System.IO;

namespace SherpaOnnxASR
{
    public static class AppSettings
    {
        // Absolute paths for model files
        public static string EncoderPath { get; set; } = "";
        public static string DecoderPath { get; set; } = "";
        public static string JoinerPath { get; set; } = "";
        public static string TokensPath { get; set; } = "";
        
        // Sample wav folder path for testing
        public static string SampleWavPath { get; set; } = "";
        
        // Microphone gain (amplification factor for quiet mics)
        public static float MicGain { get; set; } = 7.0f;
        
        public static bool IsConfigured => 
            !string.IsNullOrEmpty(EncoderPath) && File.Exists(EncoderPath) &&
            !string.IsNullOrEmpty(DecoderPath) && File.Exists(DecoderPath) &&
            !string.IsNullOrEmpty(JoinerPath) && File.Exists(JoinerPath) &&
            !string.IsNullOrEmpty(TokensPath) && File.Exists(TokensPath);
        
        // For compatibility with SherpaOnnxService
        public static string FullEncoderPath => EncoderPath;
        public static string FullDecoderPath => DecoderPath;
        public static string FullJoinerPath => JoinerPath;
        public static string FullTokensPath => TokensPath;
    }
}