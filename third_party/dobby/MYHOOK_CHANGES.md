# Isolated Dobby Dependency

Imported build-source snapshot from the existing TypeTreeRipper Dobby dependency, whose Git HEAD was `05a09ac6807a6bb1726350e40ea4b127c1c79809`. The source working tree was copied; this is not a claim that every imported byte matches that upstream commit. Apache-2.0 license is retained in LICENSE. No embedded Git checkout was copied.

Hook-only changes under MYHOOK_STRICT_ENTRY_ONLY:

- Require a four-byte near branch before committing an instruction probe; reject normal trampoline fallback.
- Propagate relocation, executable-buffer write, activation and restoration failures.
- Preserve failed restoration entries instead of claiming successful removal.
- Use an aligned atomic instruction store for four-byte ARM64 patches; report protection failures and synchronize the instruction cache after writes.
- A one-shot test fault interface rejects an owned-code write before the game probe is installed.

The companion also verifies the owned closure's incoming LR/SP convention with a live assembly control before installing a game entry probe. This establishes the local bridge extraction convention, not a game's factory signature or asset identity.

The original TypeTreeRipper dependency and RenderDoc dependencies are not modified by these local changes.
