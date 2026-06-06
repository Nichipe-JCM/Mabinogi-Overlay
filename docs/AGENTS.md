# AGENTS.md

## Communication

- Always use polite Korean honorific speech when replying to the user.
- Prefer verified facts, file paths, command results, and concrete evidence over speculation.
- For ambiguous requests, check the current repository state and work scope before editing.

## Project Scope

- This repository is for planning and implementing a Windows overlay utility that helps users monitor Mabinogi quickslot cooldowns.
- The baseline design is game-window selection, window capture, semi-automatic slot candidate selection, user calibration, and a click-through overlay.
- Do not implement game memory reads, DLL injection, render hooking, gameplay input automation, anti-cheat bypasses, or concealment techniques.

## Engineering Guidelines

- Prefer official Windows capture APIs and a separate transparent overlay window.
- Prefer C# + WPF for the MVP. Consider WinUI 3 for a later polished product phase.
- Treat slot size as a per-profile calibration value, not as a global constant.
- Treat automatic detection as an assistive feature. The user calibration editor is the core feature.
- Because display resolution, Windows scaling, and game UI scaling may vary, store coordinates relative to the selected game window.

## Git Workflow

- Run `git status --short --branch` before making changes.
- Use a purpose-specific branch for feature and documentation work.
- Do not revert user changes unless the user explicitly asks for that.
- Use short, clear commit messages that describe the intent of the change.
