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

All components are in the **Kakadoo** tab: the **Voice** panel (first), and a **Utilities** panel with Checker and Settings.

| Component | What it does | Needs |
|---|---|---|
| **Kakadoo** | Say a slider's name and a number, e.g. "height 20" or "height twenty five". | Nothing else |
| **Kakadoo Vosk** | Same as Kakadoo, fully offline with [Vosk](https://alphacephei.com/vosk/). Understands spoken numbers ("twenty five"). Can also be started by Always-On (Tether). | A Vosk model folder |
| **Kakadoo Llama** | Speak naturally, e.g. "make the building higher". A local Llama model picks the slider and the new value. Runs on CPU or GPU. | A Llama `.gguf` model file |
| **Kakadoo GPT** | Same idea with OpenAI's GPT (model `gpt-4o-mini`). | An OpenAI API key and internet |
| **Kakadoo Always-On** | Always listening. Say the catchphrase (default *"hey kakadoo"*) and then your command; the command is output as text. | Nothing else |
| **Kakadoo Simple** | Listens once and outputs what you said as text. | Nothing else |
| **Kakadoo Checker** | Tests your microphone, speech engine, models, cores and graphics card, and tells you what is missing. | Nothing else |
| **Kakadoo Settings** | One optional place for shared settings: microphone, speech model, listening preset and the listening animation. | Nothing else |

### Inputs and outputs

| Component | Inputs | Outputs |
|---|---|---|
| Kakadoo | `Sliders`, `Run`, `Whisper Model Path` (optional) | `Output` |
| Kakadoo Vosk | `Sliders`, `Vosk Model Directory Path` (Vosk folder), `Run` | `Output` |
| Kakadoo Llama | `Sliders`, `Llama Model Directory Path` (`.gguf` file), `Description` (optional), `Run`, `Device`, `Whisper Model Path` (optional) | `Output` |
| Kakadoo GPT | `Sliders`, `API Key`, `Description`, `Run`, `Whisper Model Path` (optional) | `Output` |
| Kakadoo Always-On | `Run`, `Whisper Model Path` (optional) | `Status`, `Text` |
| Kakadoo Simple | `Run`, `Whisper Model Path` (optional) | `Output`, `Command` |
| Kakadoo Checker | `Run`, `Whisper Model Path`, `Llama Model Directory Path`, `Vosk Model Directory Path` (all but `Run` optional) | `Output` |
| Kakadoo Settings | `Microphone`, `Model`, `Preset`, `Animation`, `Remember` (all optional) | `Microphones`, `Active` |

Connect a **Button** or **Boolean Toggle** to `Run`. Components start listening when `Run` turns on.

### Sliders

* Connect up to **200 named number sliders**. Give each slider a name (nickname); sliders without a name are ignored.
* **Sliders with the same name change together.**
* Values outside a slider's range are limited to it, and integer sliders get whole numbers.

### Kakadoo Llama: the `Description` and `Device` inputs

`Description` (optional, also on Kakadoo GPT) tells the model what your script and goal are, e.g. *"a tower: Height is the number of floors"*. The better the description, the better the slider it picks.

Type **`cpu`** or **`gpu`** (not case sensitive), optionally followed by `;` and the number of CPU cores to use,
for example `gpu`, `cpu;6` or `GPU;4`. Leave it empty for the default (`cpu` on Windows, `gpu` on Mac).
The **Kakadoo Checker** shows how many cores your PC has and how many it recommends.

* **GPU** uses Vulkan on Windows (NVIDIA, AMD, Intel) and Metal on Mac. Kakadoo works out how many model layers fit in
  your video memory; the rest run on the CPU. You can force a number under right-click, *GPU layers*.
* CPU and GPU cannot be switched inside one running Rhino session once a model is loaded. Restart Rhino to switch.
* With many sliders on the CPU the model has a lot to read and each command can take minutes. Use the GPU for large
  slider sets.

### Kakadoo Settings

Optional. Drop one into the document to change settings that every Kakadoo component shares. Leave an input empty to keep
its current value. Only the first Settings component in a document counts.

| Input | Meaning |
|---|---|
| `Microphone` | The microphone to listen with: its name, or its number in the `Microphones` output (0 is the first). Empty uses the system default. Changing it takes effect at once. |
| `Model` | `auto`, `fast` (smaller, quicker) or `accurate` (bigger, more precise). |
| `Preset` | A bundle of listening settings: `standard`, `quiet` (quiet room, more sensitive), `noisy` (workshop, needs a louder voice), `fast` (quick replies, smaller model) or `accurate` (waits longer, bigger model). `Model`, if set, overrides the preset's model. |
| `Animation` | Show or hide the red listening animation on all components. |
| `Remember` | `true` also keeps these values as the defaults on this PC (`~/Kakadoo/settings.txt`). Otherwise they last for the Rhino session. |

The `Microphones` output lists the microphones found on this PC; `Active` shows what is in use.

### Always-On: Trigger and Tether

A new **Kakadoo Always-On** has two inputs (`Run`, `Whisper Model Path`) and two outputs (`Status`, `Text`). `Status` shows the latest
thing it heard or did. Right-click it to choose the speech engine (**Whisper**, the default, or **Vosk** for fully offline use with a Vosk model; the second
input then becomes `Vosk Model Directory Path`) and one of two extras:

* **Trigger**: adds a `Trigger` input (a list of words) and a `Hit` output. When one of the words is heard anywhere in a
  sentence, `Hit` is `true` for the *Trigger seconds* set in the same menu (default 5), then `false`. Use it to switch other
  components, including ones that are not from Kakadoo.
* **Tether**: adds a `Tether` output. Wire it to the `Tether` input of other Kakadoo components (right-click them and tick
  *Tether input*, which replaces their `Run` input). Say the catchphrase and a command in one go, for example
  *"hey kakadoo, set the width to 30"*, and Always-On passes the sentence to every tethered component, which acts on it
  without listening again. If you say only the catchphrase, the next phrase is passed along. A tethered component shows one
  red ripple when the change is done, and a component that is still busy skips the new sentence. While Tether is on, Trigger
  is not available.

Only Always-On uses the microphone in a tethered setup, so components never compete for it. A tethered component no longer
needs its `Whisper Model Path` / `Vosk Model Directory Path` input, so those inputs are hidden while Tether is on. This works the
same for Kakadoo Vosk, so Vosk can run on its own or behind Always-On.

### Listening indicator

While a component listens, a red ripple spreads out from its edge, stronger when the microphone hears more sound. Right-click
a component and untick *Breathing animation* to turn it off for that component (a still red outline stays), or use the
`Animation` input of Kakadoo Settings for all components. Errors, such as a blocked microphone or a failed download, are shown
in plain words under the component.

### Right-click settings

| Setting | Where | Meaning |
|---|---|---|
| Keep last runs | Components with an `Output` | How many runs of messages to keep (default 4). |
| Catchphrase | Kakadoo Always-On | The words that wake it. Empty means it outputs everything you say. |
| Trigger, Trigger seconds | Kakadoo Always-On | Adds the `Trigger` input and `Hit` output, and how long `Hit` stays true. |
| Tether | Kakadoo Always-On | Adds the `Tether` output to start other Kakadoo components by voice. |
| Tether input | Kakadoo, Vosk, Llama, GPT, Simple | Replaces `Run` and the Whisper/Vosk model path inputs with a `Tether` input (Always-On does the listening). |
| Speech engine | Kakadoo Always-On | Switch between Whisper and Vosk. The model input becomes `Vosk Model Directory Path` while Vosk is selected. |
| Breathing animation | All Kakadoo components | The red listening ripple on or off. |
| GPU layers | Kakadoo Llama | `0` means as many as fit. |

---

## Install

**Requirements:** Rhino 8 for Windows (64-bit, a processor with AVX2) or Rhino 8 for Mac, and a microphone.

1. Download the package for your system from the **Releases** page (or build it, see below):
   `kakadoo-<version>-rh8_0-win.yak` or `kakadoo-<version>-rh8_0-mac.yak`. (Version 1.0.2.2 is Windows only; the latest Mac package is 1.0.2.1.)
2. Install it with Rhino's Package Manager (`_PackageManager`) or `yak install`, then **restart Rhino**.
3. In Grasshopper, open the **Kakadoo** tab. Run the **Kakadoo Checker** once. It downloads the speech model
   (a one-time download, shown with a progress bar) and reports anything that is missing.

On Mac, allow Rhino to use the microphone when macOS asks.

### Models

| Model | Used by | Where to get it |
|---|---|---|
| Whisper speech model | Kakadoo, GPT, Llama, Simple, Always-On | Downloaded automatically once to `~/Kakadoo/models` (see below). Manual download: [`ggml-small.en.bin`](https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin) (466 MB) or [`ggml-base.en.bin`](https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin) (141 MB), from the [whisper.cpp models](https://huggingface.co/ggerganov/whisper.cpp/tree/main). |
| Llama `.gguf` file | Kakadoo Llama | You provide it. The research used Nous-Hermes-Llama2-13B (Q4_0): [`nous-hermes-llama2-13b.Q4_0.gguf`](https://huggingface.co/TheBloke/Nous-Hermes-Llama2-GGUF/resolve/main/nous-hermes-llama2-13b.Q4_0.gguf) from [TheBloke/Nous-Hermes-Llama2-GGUF](https://huggingface.co/TheBloke/Nous-Hermes-Llama2-GGUF). |
| Vosk model | Kakadoo Vosk | You provide it, from <https://alphacephei.com/vosk/models>. |

The Whisper model is chosen for your PC: `small.en` (466 MB, most accurate) on a capable PC, `base.en` (141 MB, about
3x faster) on a modest one. On a development PC, small.en transcribes a short command in about half a second and
Llama 13B answers in about 4 seconds on a GPU (about 22 seconds on 8 CPU cores).

---

## Offline use

For offices where Rhino has no internet access, install the Whisper model by hand. Kakadoo looks for it in this order
and uses the first one it finds:

1. A path you set: the `Whisper Model Path` input of any component that listens with Whisper, or the `KAKADOO_WHISPER_MODEL`
   environment variable.
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
macOS support is included but has had limited testing on real Mac hardware. If macOS says a `.dylib` "Not Opened" or Whisper fails with "library load disallowed by system policy", Kakadoo now clears the quarantine flag and signs its libraries itself on first use. If it still fails, run this in Terminal (adjust the path to your Kakadoo install) and restart Rhino:

```
xattr -dr com.apple.quarantine ~/Library/"Application Support"/McNeel/Rhinoceros/packages/8.0/Kakadoo
find ~/Library/"Application Support"/McNeel/Rhinoceros/packages/8.0/Kakadoo -name "*.dylib" -exec codesign --force --sign - {} \;
```

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

---

## Research

Kakadoo was developed at the Bauhaus-Universität Weimar and the City Intelligence Lab (AIT).

**Paper** (for citations):

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

[LGPL-3.0](LICENSE) © 2024 Alp Okan Atakan. Third-party software and models have their own licences, see
[THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).
