# Sherpa Live Caption (Windows GPU Edition)

This repository is a fork of `sherpa-onnx` created to implement a local, real-time speech recognition system similar to **Windows Live Captions**.

Inspired (and slightly shocked) by the performance and utility of Windows Live Captions, this project aims to replicate that experience using the open-source **Sherpa-Onnx** engine. It focuses on building a native Windows application with full GPU acceleration (CUDA) for low-latency, high-accuracy streaming transcription.

## Demo example in Korean
- This is running on GPU, but CPU performance is just as good! :) Nice work, the Sherpa-onnx team!

![Sherpa Live Caption GUI Demo](gui_demo.gif)


---

## 1. Prerequisites

You can use your own versions, but **CUDA 9+ is required for ONNX Runtime 1.23**. The instructions below are tested with the following configuration:

1. **CMake**: Install the latest version and add it to your System PATH.
2. **Visual Studio 2022**: Install with the **"Desktop development with C++"** workload.
3. **Git for Windows**: Required for submodule management.
4. **CUDA Toolkit 12.1**: [Download Link](https://developer.nvidia.com/cuda-12-1-1-download-archive)
* *Note: Ensure "Visual Studio Integration" is checked during installation.*


5. **cuDNN 9.1.7 for CUDA 12.x**: [Download Link](https://developer.nvidia.com/cudnn-downloads?target_os=Windows&target_arch=x86_64&target_version=11&target_type=exe_local)
* **Manual Installation Steps (Copy files):**
* `C:\Program Files\NVIDIA\CUDNN\v9.17\bin\12.9`  `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.1\bin`
* `C:\Program Files\NVIDIA\CUDNN\v9.17\lib\12.9\x64`  `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.1\lib\x64`
* `C:\Program Files\NVIDIA\CUDNN\v9.17\include\12.9`  `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.1\include`


* **System PATH**: Ensure `bin`, `lib`, and `cuda_home` are added to your System PATH.



## 2. Prepare Source

```powershell
# Fetch submodules
git submodule update --init --recursive

```

## 3. Build Process (Sherpa-Onnx Core)

Run the following commands in the **Developer PowerShell for VS 2022**:

```powershell
cd third_party/sherpa-onnx
mkdir build
cd build

# Configure with CUDA/cuDNN paths
cmake -DSHERPA_ONNX_ENABLE_GPU=ON `
      -DBUILD_SHARED_LIBS=ON `
      -DCMAKE_BUILD_TYPE=Release `
      -DCUDNN_LIBRARY="C:/Program Files/NVIDIA GPU Computing Toolkit/CUDA/v12.1/lib/x64/cudnn.lib" `
      -DCUDNN_INCLUDE_DIR="C:/Program Files/NVIDIA GPU Computing Toolkit/CUDA/v12.1/include" `
      -DCUDA_PATH="C:/Program Files/NVIDIA GPU Computing Toolkit/CUDA/v12.1" `
      ..      

# Build the core engine
cmake --build . --config Release --target sherpa-onnx-core

```

## 4. Deployment Checklist

To run this application on other Windows 11 machines (without a full CUDA toolkit installation), you must bundle the following DLLs found in the build folders:

* [ ] `sherpa-onnx-c-api.dll`
* [ ] `onnxruntime.dll`
* [ ] `onnxruntime_providers_cuda.dll` *(Found in `sherpa-onnx/build/_deps/onnxruntime-src/lib/`)*
* [ ] `onnxruntime_providers_shared.dll` *(Found in `sherpa-onnx/build/_deps/onnxruntime-src/lib/`)*
* [ ] `onnxruntime_providers_tensorrt.dll` *(Found in `sherpa-onnx/build/_deps/onnxruntime-src/lib/`)*

## 5. Verify GPU Acceleration (Console Test)

This step verifies that the build can utilize the GPU for inference.

**Location:** `third_party/sherpa-onnx/dotnet-examples/online-decode-files`

### Step A: Prepare Models

1. Download **Streaming Zipformer** models from [sherpa-onnx ASR models](https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models).
2. Extract the model files and set the path in your PowerShell session:
```powershell
$modelDir = "C:\path\to\your\model_folder"

```



### Step B: Build and Run

```powershell
cd third_party/sherpa-onnx/dotnet-examples/online-decode-files
# Apply patch for the example
cp ../../../../patches/sherpa-onnx/dotnet-example/online-decode-files/* .

dotnet build

# Verify DLLs exist (Ensure the 5 DLLs from Section 4 are present)
ls bin\Release\net8.0\*.dll

# Run decoding with CUDA provider
dotnet run -c Debug -- `
  --tokens="$modelDir\tokens.txt" `
  --encoder="$modelDir\encoder.onnx" `
  --decoder="$modelDir\decoder.onnx" `
  --joiner="$modelDir\joiner.onnx" `
  --provider=cuda `
  --files="$modelDir\test_wavs\0.wav" "$modelDir\test_wavs\1.wav"

```

## 6. Publish Console App

To create a standalone distribution of the console test app:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish

# Copy required native libraries
cp ..\..\build\bin\Release\*.dll .\publish\
cp ..\..\build\_deps\onnxruntime-src\lib\*.dll .\publish\

# Note: cuDNN DLLs must also be present in the publish directory or PATH.

```

**Run the published app:**

```powershell
.\publish\online-decode-files.exe `
  --tokens="$modelDir\tokens.txt" `
  --encoder="$modelDir\encoder.onnx" `
  --decoder="$modelDir\decoder.onnx" `
  --joiner="$modelDir\joiner.onnx" `
  --provider=cuda `
  --files="$modelDir\test_wavs\0.wav" "$modelDir\test_wavs\1.wav"

```

## 7. GUI Application (Sherpa Live Caption)

This is the main WPF application that mimics the Windows Live Caption interface.

### Setup

Copy the GUI project files from the patches directory:

```powershell
Copy-Item -Recurse -Force "patches/sherpa-onnx/dotnet-examples/online-decode-gui" "third_party/sherpa-onnx/dotnet-examples/"

```

### Build & Run (Development)

```powershell
cd third_party/sherpa-onnx/dotnet-examples/online-decode-gui
dotnet restore
dotnet build -c Release
dotnet run -c Release

```

### Publish (Standalone EXE)

This creates a single executable with all dependencies included.

```powershell
cd third_party/sherpa-onnx/dotnet-examples/online-decode-gui

# Publish the .NET application
dotnet publish -c Release -r win-x64 --self-contained true -o ./publish

# MANUALLY Copy the GPU native DLLs to the publish folder
# (These are not automatically included by dotnet publish)
Copy-Item -Path "..\..\build\bin\Release\*.dll" -Destination "./publish" -Force
Copy-Item -Path "..\..\build\_deps\onnxruntime-src\lib\*.dll" -Destination "./publish" -Force

```

### Notes

* The output folder `./publish` contains the standalone `SherpaOnnxASR.exe`.
* Ensure **CUDA/cuDNN DLLs** are available in the system PATH or copied into the same folder as the executable for GPU acceleration to work on target machines.
