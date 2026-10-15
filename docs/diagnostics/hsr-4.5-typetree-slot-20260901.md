# HSR 4.5 TypeTree Slot Evidence

## Scope

This note records the slot selection used by the HSR 4.5 MuMu observer. It is
not a field-level TypeTree result and does not enable the native TypeTree call.

## Evidence

- The guest package reports Unity `2019.4.34f1`.
- `TypeTreeRipper/source/Object.hpp` uses the `V2017_1_0` runtime Object layout
  for this version because there is no later explicit 2019 runtime Object
  declaration.
- The historical layout names these neighboring slots as transfer-related
  methods, but the current HSR 4.5 runtime evidence does not establish the
  semantic type of any one slot from its offset alone.
- The current HSR 4.5 `Material` vtable probe contains readable guest
  function targets at both `+0x90` and `+0x98`. The probe proves the slots are
  mapped, but does not prove that either function was invoked.

## Change

The safe transfer observer still defaults to `+0xa0`, and the HSR 4.5 profile
sets `observeVtableOffset` to `0xa0`, because that is the slot used by the
existing observation session. This is an observation setting, not a confirmed
`GenerateTypeTreeTransfer` slot. The native TypeTree generation path remains
disabled unless separate target-specific ABI evidence is obtained.

## Verification boundary

A successful observer installation only proves that the selected vtable slot
was patched and the original target was retained. Field-level TypeTree is
considered available only when a runtime event contains validated node and
string arrays; empty catalogs and raw vtable entries do not satisfy that
condition.
