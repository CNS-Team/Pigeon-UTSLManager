# VtNetCore memory fix

MIT source from https://github.com/darrenstarr/VtNetCore at commit
`39237314c5e6e90be6a0a0cfa29e1f2caa8d1775` (2020-09-08),
"Make Regex fields static to improve performance".

The previously shipped NuGet 1.0.30 constructs two compiled Regex instances
inside every TerminalColor, including copies made for true-color characters.
Live heap sampling found a large population of DynamicMethod/Regex objects.
This pinned upstream revision shares those immutable Regex instances across
all colors; it does not replace the VT parser or remove ANSI/color features.

The `.cs` files match `upstream-source-manifest.json` exactly. The local project
targets the same netstandard2.0 API surface, keeps AssemblyVersion 1.0.30.0,
and identifies the repaired build as FileVersion 1.0.30.1. Original license
and copyright are preserved. No runtime binary patching or shell hooks.
