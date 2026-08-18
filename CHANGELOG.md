# Changelog

Developed by saelac with assistance from ChatGPT.

## 1.17.1

- Rebuilt against Modding Tools Menu 1.9.1 and its new
  `Saelac.Silverpine.ModdingTools` BepInEx identity.
- Declared Modding Tools 1.9.1 as the minimum supported framework version so
  BepInEx reports an outdated dependency instead of silently skipping or
  misloading the plugin.
- No relationship save schema, configuration key, or gameplay behavior was
  changed.

## 1.17.0

- Added save-scoped, opt-in NPC relationship progression from Enemy through
  Married.
- Added neutral, positive, and negative conversation assessment with guarded
  promotion queries.
- Added in-dialog NPC marriage proposals, marriage/divorce world knowledge,
  romantic rejection ceilings, and polygamous spouse titles.
- Added stable custom-NPC identity handling and per-run persistence tied to
  normal game saves.
- Added optional Runtime NPC Editor controls and public NPC-to-player proposal
  integration hooks.

Public releases are built in optimized Release configuration without debug
symbols.
