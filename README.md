<p align="center">
  <img src="assets/icon.png" alt="Kakadoo logo" width="120">
</p>

# Kakadoo

**Manipulate Grasshopper sliders with your voice.**

Kakadoo is a plugin for [Grasshopper](https://www.grasshopper3d.com/) in Rhino 8 (Windows and Mac). Say what you want
("make the building higher", "set the width to 30") and Kakadoo moves the right sliders and the model updates right away.
Speech recognition and the language models run **on your own computer, offline and free**. No account or API key is needed
(only the optional *Kakadoo GPT* component uses the internet).

Kakadoo was built to make co-design sessions easier: people can steer a parametric model by talking, without knowing
Grasshopper. It is described in the paper listed under [Research](#research).

---

## Components

All components are in the **Kakadoo** tab, **Voice** panel.

| Component | What it does | Needs |
|---|---|---|
| **Kakadoo** | Say a slider's name and a number, e.g. "height 20". | Nothing else |
| **Kakadoo Vosk** | Same as Kakadoo, fully offline with [Vosk](https://alphacephei.com/vosk/). Understands spoken numbers ("twenty five"). | A Vosk model folder |
| **Kakadoo Llama** | Speak naturally, e.g. "make the building higher". A local Llama model picks the slider and the new value. Runs on CPU or GPU. | A Llama `.gguf` model file |
| **Kakadoo GPT** | Same idea with OpenAI's GPT. | An OpenAI API key and internet |
| **Kakadoo Always-On** | Always listening. Say the catchphrase (default *"hey kakadoo"*) and then your command; the command is output as text. | Nothing else |
| **Kakadoo Simple** | Listens once and outputs what you said as text. | Nothing else |
| **Kakadoo Checker** | Tests your microphone, speech engine, models, cores and graphics card, and tells you what is missing. | Nothing else |

### Inputs and outputs

| Component | Inputs | Outputs |
|---|---|---|
| Kakadoo | `Sliders`, `Run` | `Output` |
| Kakadoo Vosk | `Sliders`, `Model Directory Path` (Vosk folder), `Run` | `Output` |
| Kakadoo Llama | `Sliders`, `Model Directory Path` (`.gguf` file), `Run`, `Device` | `Output` |
| Kakadoo GPT | `Sliders`, `API Key`, `Description`, `Run` | `Output` |
| Kakadoo Always-On | `run`, `Model Path` (optional) | `info`, `text` |
| Kakadoo Simple | `Run` | `Output`, `Command` |
| Kakadoo Checker | `Run`, `Model Directory Path`, `Vosk Model Path` | `Output` |

Connect a **Button** or **Boolean Toggle** to `Run`. Components start listening when `Run` turns on.

### Sliders

* Connect up to **200 named number sliders**. Give each slider a name (nickname); sliders without a name are ignored.
* **Sliders with the same name change together.**
* Values outside a slider's range are limited to it, and integer sliders get whole numbers.

### Kakadoo Llama: the `Device` input

Type **`cpu`** or **`gpu`** (not case sensitive), optionally followed by `;` and the number of CPU cores to use,
for example `gpu`, `cpu;6` or `GPU;4`. Leave it empty for the default (`cpu` on Windows, `gpu` on Mac).
The **Kakadoo Checker** shows how many cores your PC has and how many it recommends.

* **GPU** uses Vulkan on Windows (NVIDIA, AMD, Intel) and Metal on Mac. Kakadoo works out how many model layers fit in
  your video memory; the rest run on the CPU. You can force a number under right-click, *GPU layers*.
* CPU and GPU cannot be switched inside one running Rhino session once a model is loaded. Restart Rhino to switch.
* With many sliders on the CPU the model has a lot to read and each command can take minutes. Use the GPU for large
  slider sets.

### Right-click settings

| Setting | Where | Meaning |
|---|---|---|
| Keep last runs | Components with an `Output` | How many runs of messages to keep (default 4). |
| Catchphrase | Kakadoo Always-On | The words that wake it. Empty means it outputs everything you say. |
| Whisper model file or folder | All components that use Whisper except Always-On | Use a model you installed yourself (see [Offline use](#offline-use)). |
| GPU layers | Kakadoo Llama | `0` means as many as fit. |

---

## Install

**Requirements:** Rhino 8 for Windows (64-bit, a processor with AVX2) or Rhino 8 for Mac, and a microphone.

1. Download the package for your system from the **Releases** page (or build it, see below):
   `kakadoo-<version>-rh8_0-win.yak` or `kakadoo-<version>-rh8_0-mac.yak`.
2. Install it with Rhino's Package Manager (`_PackageManager`) or `yak install`, then **restart Rhino**.
3. In Grasshopper, open the **Kakadoo** tab. Run the **Kakadoo Checker** once. It downloads the speech model
   (a one-time download, shown with a progress bar) and reports anything that is missing.

On Mac, allow Rhino to use the microphone when macOS asks.

### Models

| Model | Used by | Where to get it |
|---|---|---|
| Whisper speech model | Kakadoo, GPT, Llama, Simple, Always-On | Downloaded automatically once to `~/Kakadoo/models` (see below). |
| Llama `.gguf` file | Kakadoo Llama | You provide it. The research used Nous-Hermes-Llama2-13B (Q4_0). |
| Vosk model | Kakadoo Vosk | You provide it, from <https://alphacephei.com/vosk/models>. |

The Whisper model is chosen for your PC: `small.en` (466 MB, most accurate) on a capable PC, `base.en` (141 MB, about
3x faster) on a modest one. On a development PC, small.en transcribes a short command in about half a second and
Llama 13B answers in about 4 seconds on a GPU (about 22 seconds on 8 CPU cores).

---

## Offline use

For offices where Rhino has no internet access, install the Whisper model by hand. Kakadoo looks for it in this order
and uses the first one it finds:

1. A path you set: the `Model Path` input of **Always-On**, or the right-click setting *Whisper model file or folder*
   (saved in `~/Kakadoo/settings.txt`), or the `KAKADOO_WHISPER_MODEL` environment variable.
   The path can be a model file or a folder containing `ggml-*.bin` files.
2. A `models` folder next to the plugin (handy for IT deployment).
3. `~/Kakadoo/models/` (Windows: `C:\Users\<you>\Kakadoo\models\`), where downloads go.
4. A one-time download from Hugging Face.

Get the files from <https://huggingface.co/ggerganov/whisper.cpp/tree/main> (for example `ggml-base.en.bin`).

If Whisper cannot start, Windows uses its built-in speech recognition and Mac uses Vosk (if a Vosk model is installed),
and the component tells you so. Those are less accurate.

| Environment variable | Meaning |
|---|---|
| `KAKADOO_WHISPER_MODEL` | A model name (`base.en`, `small`, ...) or a path to a model file or folder. |
| `KAKADOO_ENGINE` | Use one engine only: `whisper`, `system` (Windows), or `vosk`. |
| `KAKADOO_OFFLINE` | Set to `1` to never download anything. |
| `KAKADOO_VOSK_MODEL` | Folder of the Vosk model used as the Mac backup engine. |

---

## Troubleshooting

Run the **Kakadoo Checker**. Each check shows `[OK]`, `[FAILED]` or `[SKIPPED]` with the reason.

* **"No microphone found, or access is blocked"** : allow desktop apps to use the microphone (Windows: Settings > Privacy &
  security > Microphone; Mac: System Settings > Privacy & Security > Microphone).
* **Model download fails** : see [Offline use](#offline-use).
* **Speech is mis-heard** : use the `small.en` model, speak after the first half second of silence, and give sliders
  clear, distinct names. Kakadoo passes the slider names to Whisper as a hint.
* **Llama is slow** : choose `gpu` in the `Device` input, use a smaller model, or connect fewer sliders.

Notes: the plugin does not load when Rhino 8 is set to the .NET Framework runtime (use the default .NET 7/8 runtime).
macOS support is included but has not yet been tested on real Mac hardware.

---

## Build from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) and Rhino 8.

```
git clone https://github.com/AlpachinoOA/Kakadoo.git
cd Kakadoo/src/Kakadoo
dotnet build -c Release
```

The plugin is built for `net7.0-windows` and `net7.0` (Mac). If Rhino 8 is installed, the build also creates the Yak
packages `kakadoo-<version>-rh8_0-win.yak` and `-mac.yak` in `bin/Release`.

### Project structure

```
Kakadoo/
├── README.md
├── LICENSE
├── THIRD_PARTY_NOTICES.txt
├── Kakadoo.sln
├── assets/
│   ├── icon.png                 package icon
│   └── logos/                   component logos (SVG)
└── src/Kakadoo/
    ├── Kakadoo.csproj
    ├── KakadooInfo.cs           plugin information shown in Grasshopper
    ├── Components/              the Grasshopper components
    ├── Speech/                  microphone, Whisper, Vosk, backup engines, catchphrase, spoken numbers
    ├── Llm/                     native loading for the local Llama models
    ├── Common/                  slider handling, settings, hardware detection, icons
    ├── UI/                      download progress bar on the canvas
    ├── Resources/               component icons
    └── Properties/
```

---

## Research

Kakadoo was developed at the Bauhaus-Universität Weimar and the City Intelligence Lab (AIT).

**Paper** (please cite this):

> Atakan, A. O., Kostourou, F., Sandor, V., Seer, S. and Koenig, R. (2025) "Kakadoo: LLM enabled speech interface for
> enhanced human-computer interaction", in *Confluence: Proceedings of the 43rd Conference on Education and Research in
> Computer Aided Architectural Design in Europe (eCAADe 2025)*, Volume 2, Ankara, 1-5 September 2025, pp. 661-670.
> <https://doi.org/10.52842/conf.ecaade.2025.2.661>  ·  [CUMINCAD](https://papers.cumincad.org/cgi-bin/works/paper/ecaade2025_348)

```bibtex
@inproceedings{atakan2025kakadoo,
  title     = {Kakadoo: LLM enabled speech interface for enhanced human-computer interaction},
  author    = {Atakan, Alp Okan and Kostourou, Fani and Sandor, Viktoria and Seer, Stefan and Koenig, Reinhard},
  booktitle = {Confluence: Proceedings of the 43rd Conference on Education and Research in Computer Aided Architectural Design in Europe (eCAADe 2025)},
  volume    = {2},
  pages     = {661--670},
  address   = {Ankara},
  year      = {2025},
  doi       = {10.52842/conf.ecaade.2025.2.661}
}
```

## License

[MIT](LICENSE) © 2024 Alp Okan Atakan. Third-party software and models have their own licences, see
[THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).
