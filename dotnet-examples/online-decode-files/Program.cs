// Copyright (c)  2023  Xiaomi Corporation
// Copyright (c)  2023 by manyeyes
//
// This file shows how to use a streaming model to decode files
// Please refer to
// https://k2-fsa.github.io/sherpa/onnx/pretrained_models/online-transducer/zipformer-transducer-models.html
// to download streaming models

using CommandLine;
using CommandLine.Text;
using SherpaOnnx;
using Microsoft.ML.OnnxRuntime; // NuGet 패키지 필요

class OnlineDecodeFiles
{
  class Options
  {
    [Option(Required = true, HelpText = "Path to tokens.txt")]
    public string Tokens { get; set; } = string.Empty;

    [Option(Required = false, Default = "cpu", HelpText = "Provider, e.g., cpu, coreml")]
    public string Provider { get; set; } = string.Empty;

    [Option(Required = false, HelpText = "Path to transducer encoder.onnx")]
    public string Encoder { get; set; } = string.Empty;

    [Option(Required = false, HelpText = "Path to transducer decoder.onnx")]
    public string Decoder { get; set; } = string.Empty;

    [Option(Required = false, HelpText = "Path to transducer joiner.onnx")]
    public string Joiner { get; set; } = string.Empty;

    [Option("paraformer-encoder", Required = false, HelpText = "Path to paraformer encoder.onnx")]
    public string ParaformerEncoder { get; set; } = string.Empty;

    [Option("paraformer-decoder", Required = false, HelpText = "Path to paraformer decoder.onnx")]
    public string ParaformerDecoder { get; set; } = string.Empty;

    [Option("zipformer2-ctc", Required = false, HelpText = "Path to zipformer2 CTC onnx model")]
    public string Zipformer2Ctc { get; set; } = string.Empty;

    [Option("t-one-ctc", Required = false, HelpText = "Path to T-one CTC onnx model")]
    public string ToneCtc { get; set; } = string.Empty;

    [Option("num-threads", Required = false, Default = 1, HelpText = "Number of threads for computation")]
    public int NumThreads { get; set; } = 1;

    [Option("decoding-method", Required = false, Default = "greedy_search",
            HelpText = "Valid decoding methods are: greedy_search, modified_beam_search")]
    public string DecodingMethod { get; set; } = "greedy_search";

    [Option(Required = false, Default = false, HelpText = "True to show model info during loading")]
    public bool Debug { get; set; } = false;

    [Option("sample-rate", Required = false, Default = 16000, HelpText = "Sample rate of the data used to train the model")]
    public int SampleRate { get; set; } = 16000;

    [Option("max-active-paths", Required = false, Default = 4,
        HelpText = @"Used only when --decoding--method is modified_beam_search.
It specifies number of active paths to keep during the search")]
    public int MaxActivePaths { get; set; } = 4;

    [Option("enable-endpoint", Required = false, Default = false,
        HelpText = "True to enable endpoint detection.")]
    public bool EnableEndpoint { get; set; } = false;

    [Option("rule1-min-trailing-silence", Required = false, Default = 2.4F,
        HelpText = @"An endpoint is detected if trailing silence in seconds is
larger than this value even if nothing has been decoded. Used only when --enable-endpoint is true.")]
    public float Rule1MinTrailingSilence { get; set; } = 2.4F;

    [Option("rule2-min-trailing-silence", Required = false, Default = 1.2F,
        HelpText = @"An endpoint is detected if trailing silence in seconds is
larger than this value after something that is not blank has been decoded. Used
only when --enable-endpoint is true.")]
    public float Rule2MinTrailingSilence { get; set; }  = 1.2F;

    [Option("rule3-min-utterance-length", Required = false, Default = 20.0F,
        HelpText = @"An endpoint is detected if the utterance in seconds is
larger than this value. Used only when --enable-endpoint is true.")]
    public float Rule3MinUtteranceLength { get; set; } = 20.0F;

    [Option("hotwords-file", Required = false, Default = "", HelpText = "Path to hotwords.txt")]
    public string HotwordsFile { get; set; } = string.Empty;

    [Option("hotwords-score", Required = false, Default = 1.5F, HelpText = "hotwords score")]
    public float HotwordsScore { get; set; } = 1.5F;

    [Option("rule-fsts", Required = false, Default = "",
            HelpText = "If not empty, path to rule fst for inverse text normalization")]
    public string RuleFsts { get; set; } = string.Empty;

    [Option("files", Required = true, HelpText = "Audio files for decoding")]
    public IEnumerable<string> Files { get; set; } = new string[] {};
	
	[Option("show-intermediate", Required = false, Default = false, 
		HelpText = "If true, then partial results are printed.")]
	public bool ShowIntermediate { get; set; }	
  }

  static void Main(string[] args)
  {
    var parser = new CommandLine.Parser(with => with.HelpWriter = null);
    var parserResult = parser.ParseArguments<Options>(args);

    parserResult
      .WithParsed<Options>(options => Run(options))
      .WithNotParsed(errs => DisplayHelp(parserResult, errs));
  }

  private static void DisplayHelp<T>(ParserResult<T> result, IEnumerable<Error> errs)
  {
    string usage = @"
(1) Streaming transducer models

dotnet run \
  --tokens=./sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20/tokens.txt \
  --encoder=./sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20/encoder-epoch-99-avg-1.onnx \
  --decoder=./sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20/decoder-epoch-99-avg-1.onnx \
  --joiner=./sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20/joiner-epoch-99-avg-1.onnx \
  --num-threads=2 \
  --decoding-method=modified_beam_search \
  --debug=false \
  --files ./sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20/test_wavs/0.wav \
  ./sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20/test_wavs/1.wav

(2) Streaming Zipformer2 Ctc models

dotnet run -c Release \
  --tokens ./sherpa-onnx-streaming-zipformer-ctc-multi-zh-hans-2023-12-13/tokens.txt \
  --zipformer2-ctc ./sherpa-onnx-streaming-zipformer-ctc-multi-zh-hans-2023-12-13/ctc-epoch-20-avg-1-chunk-16-left-128.onnx \
  --files ./sherpa-onnx-streaming-zipformer-ctc-multi-zh-hans-2023-12-13/test_wavs/DEV_T0000000000.wav \
  ./sherpa-onnx-streaming-zipformer-ctc-multi-zh-hans-2023-12-13/test_wavs/DEV_T0000000001.wav \
  ./sherpa-onnx-streaming-zipformer-ctc-multi-zh-hans-2023-12-13/test_wavs/DEV_T0000000002.wav \
  ./sherpa-onnx-streaming-zipformer-ctc-multi-zh-hans-2023-12-13/test_wavs/TEST_MEETING_T0000000113.wav \
  ./sherpa-onnx-streaming-zipformer-ctc-multi-zh-hans-2023-12-13/test_wavs/TEST_MEETING_T0000000219.wav \
  ./sherpa-onnx-streaming-zipformer-ctc-multi-zh-hans-2023-12-13/test_wavs/TEST_MEETING_T0000000351.wav

(3) Streaming Paraformer models
dotnet run \
  --tokens=./sherpa-onnx-streaming-paraformer-bilingual-zh-en/tokens.txt \
  --paraformer-encoder=./sherpa-onnx-streaming-paraformer-bilingual-zh-en/encoder.int8.onnx \
  --paraformer-decoder=./sherpa-onnx-streaming-paraformer-bilingual-zh-en/decoder.int8.onnx \
  --num-threads=2 \
  --decoding-method=greedy_search \
  --debug=false \
  --files ./sherpa-onnx-streaming-paraformer-bilingual-zh-en/test_wavs/0.wav \
  ./sherpa-onnx-streaming-paraformer-bilingual-zh-en/test_wavs/1.wav

Please refer to
https://k2-fsa.github.io/sherpa/onnx/pretrained_models/online-transducer/index.html
https://k2-fsa.github.io/sherpa/onnx/pretrained_models/online-paraformer/index.html
https://k2-fsa.github.io/sherpa/onnx/pretrained_models/online-ctc/index.html
to download pre-trained streaming models.
";

    var helpText = HelpText.AutoBuild(result, h =>
    {
      h.AdditionalNewLineAfterOption = false;
      h.Heading = usage;
      h.Copyright = "Copyright (c) 2023 Xiaomi Corporation";
      return HelpText.DefaultParsingErrorsHandler(result, h);
    }, e => e);
    Console.WriteLine(helpText);
  }

  private static void Run(Options options)
  {
    var config = new OnlineRecognizerConfig();
    config.FeatConfig.SampleRate = options.SampleRate;
    config.FeatConfig.FeatureDim = 80;

    config.ModelConfig.Transducer.Encoder = options.Encoder;
    config.ModelConfig.Transducer.Decoder = options.Decoder;
    config.ModelConfig.Transducer.Joiner = options.Joiner;
    config.ModelConfig.Paraformer.Encoder = options.ParaformerEncoder;
    config.ModelConfig.Paraformer.Decoder = options.ParaformerDecoder;
    config.ModelConfig.Zipformer2Ctc.Model = options.Zipformer2Ctc;
    config.ModelConfig.ToneCtc.Model = options.ToneCtc;

    config.ModelConfig.Tokens = options.Tokens;
	string given_provider = options.Provider.ToLower().Contains("gpu") ? "cuda" : options.Provider;
	config.ModelConfig.Provider = given_provider;
    config.ModelConfig.NumThreads = options.NumThreads;
    config.ModelConfig.Debug = options.Debug ? 1 : 0;

    config.DecodingMethod = options.DecodingMethod;
    config.MaxActivePaths = options.MaxActivePaths;
    config.EnableEndpoint = options.EnableEndpoint ? 1 : 0;

    config.Rule1MinTrailingSilence = options.Rule1MinTrailingSilence;
    config.Rule2MinTrailingSilence = options.Rule2MinTrailingSilence;
    config.Rule3MinUtteranceLength = options.Rule3MinUtteranceLength;
    config.HotwordsFile = options.HotwordsFile;
    config.HotwordsScore = options.HotwordsScore;
    config.RuleFsts = options.RuleFsts;

    // 1. 현재 로드된 ONNX Runtime이 진짜로 CUDA를 보고 있는지 확인
	var providers = Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetAvailableProviders();
    bool isCudaAvailable = providers.Any(p => p.Contains("CUDA", StringComparison.OrdinalIgnoreCase));

    // 2. Recognizer 생성
    var recognizer = new OnlineRecognizer(config);

    var files = options.Files.ToArray();
    var streams = new List<OnlineStream>();
    var audioDurations = new List<double>(); // 파일별 음성 길이를 저장

    streams.EnsureCapacity(files.Length);
    audioDurations.EnsureCapacity(files.Length);

    // 1. 스트림 생성 및 오디오 데이터 입력
    for (int i = 0; i != files.Length; ++i)
    {
      var s = recognizer.CreateStream();
      var waveReader = new WaveReader(files[i]);
      
      // 음성 길이 계산 (샘플 수 / 샘플 레이트)
      double duration = (double)waveReader.Samples.Length / waveReader.SampleRate;
      audioDurations.Add(duration);

      var leftPadding = new float[(int)(waveReader.SampleRate * 0.3)];
      s.AcceptWaveform(waveReader.SampleRate, leftPadding);
      s.AcceptWaveform(waveReader.SampleRate, waveReader.Samples);
      var tailPadding = new float[(int)(waveReader.SampleRate * 0.6)];
      s.AcceptWaveform(waveReader.SampleRate, tailPadding);

      s.InputFinished();
      streams.Add(s);
    }

	// 2. 디코딩 및 실시간 결과 확인
	var sw = System.Diagnostics.Stopwatch.StartNew();

	// 각 스트림의 마지막 텍스트 상태를 저장하기 위한 배열
	string[] lastTexts = new string[streams.Count];
	Array.Fill(lastTexts, "");

	bool[] isFinalPrinted = new bool[streams.Count];

	Console.WriteLine("\n--- Start decoding ---");

	while (true)
	{
	    var readyStreams = streams.Where(s => recognizer.IsReady(s)).ToList();
	    
	    // 모든 파일의 출력이 끝났고, 더 이상 처리할 스트림도 없다면 루프 종료
	    if (!readyStreams.Any() && isFinalPrinted.All(printed => printed))
	    {
	        break;
	    }

	    // 연산 수행
	    if (readyStreams.Any())
	    {
	        recognizer.Decode(readyStreams);
	    }

	    // 2. 루프 내부에서 각 파일별로 완료 여부 체크
	    for (int i = 0; i < streams.Count; i++)
	    {
	        // 아직 최종 결과를 안 찍었는데, 해당 스트림이 더 이상 Ready가 아니면 (디코딩 완료)
	        if (!isFinalPrinted[i] && !recognizer.IsReady(streams[i]))
	        {
	            var result = recognizer.GetResult(streams[i]);
	            
	            // --- 해당 파일에 대한 최종 결과 출력 ---
	            Console.WriteLine($"\n" + new string('=', 40));
	            Console.WriteLine($"[INDEX {i}] 최종 완료: {files[i]}");
	            Console.WriteLine($"[TEXT {i}]: {result.Text}");
	            
	            // 타임스탬프 상세 출력
	            Console.WriteLine($"[TIMESTAMPS {i}]:");
	            for (int j = 0; j < result.Tokens.Length; j++)
	            {
	                Console.Write($"[{result.Timestamps[j]:F2}s:{result.Tokens[j]}] ");
	                if ((j + 1) % 5 == 0) Console.WriteLine();
	            }
	            Console.WriteLine("\n" + new string('=', 40));

	            isFinalPrinted[i] = true; // 중복 출력 방지
	        }
	        // 완료 전에는 중간 결과 보여주기 (옵션이 켜져 있을 때)
			else if (options.ShowIntermediate && !isFinalPrinted[i])
			{
				var result = recognizer.GetResult(streams[i]);
				if (!string.IsNullOrEmpty(result.Text) && result.Text != lastTexts[i])
				{
					// 1. 현재까지 인식된 모든 토큰과 타임스탬프를 한 줄의 문자열로 조합
					var timedTokens = new System.Text.StringBuilder();
					for (int j = 0; j < result.Tokens.Length; j++)
					{
						string token = result.Tokens[j];
						float time = result.Timestamps[j];
						
						// 최종 결과와 동일한 포맷: [시간s:토큰]
						timedTokens.Append($"[{time:F2}s:{token}] ");
					}

					// 2. 조합된 전체 문장 출력
					Console.WriteLine($"[진행중 {i}] {timedTokens.ToString()}");
					
					lastTexts[i] = result.Text;
				}				
			}
	    }
	}	

    sw.Stop(); // 디코딩 종료
	
	

    double totalElapsedSeconds = sw.Elapsed.TotalSeconds;

    // 3. 결과 표시 및 RTF 계산 출력
    Console.WriteLine("\n" + new string('=', 50));
    Console.WriteLine("ASR Decoding Results & Performance");
    Console.WriteLine(new string('=', 50));

    double totalAudioDuration = audioDurations.Sum();

    for (int i = 0; i != files.Length; ++i)
    {
      var r = recognizer.GetResult(streams[i]);
      Console.WriteLine($"File {i}: {files[i]}");
      Console.WriteLine($"Text {i}: {r.Text}");
      Console.WriteLine($"Audio Duration {i}: {audioDurations[i]:F2}s");
      Console.WriteLine("--------------------");
    }

    // 전체 성능 지표 출력
    double rtf = totalElapsedSeconds / totalAudioDuration;
    Console.WriteLine($"Total Audio Duration : {totalAudioDuration:F2} s");
    Console.WriteLine($"Total Processing Time: {totalElapsedSeconds:F4} s");
    Console.WriteLine($"RTF (Processing/Audio): {rtf:F4}");
	
	string actualDevice = "CPU";
    
    // 사용자가 cuda를 요청했고, 시스템에 cuda 프로바이더가 로드되었을 때만 GPU로 인정
    if (given_provider == "cuda" && isCudaAvailable)
    {
        actualDevice = "GPU (CUDA)";
    }

    Console.WriteLine($"\n" + new string('-', 30));
    Console.WriteLine($"[Requested Device]: {options.Provider}");
    Console.WriteLine($"[Available Providers]: {string.Join(", ", providers)}");
    Console.WriteLine($"[Actual Execution]: {actualDevice}");
    Console.WriteLine($"[RTF Performance]: {rtf:F4}");
    
    Console.WriteLine(new string('=', 50));
  }
}


