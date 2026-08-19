# Vendored ramp body PNGs (LW-247 D4)

STATUS: CONTRACT. Sixteen (id, surface) bodies the ported ramp engine cannot re-render from
vanilla + the committed tables (census2 BODY-VEND verdicts,
tools/probes/lw247_census2_result.json). Extracted by tools/probes/lw247_extract_bodies.py:
each file is the target texture restored to vanilla OUTSIDE the smoothed vanilla silhouette
(the same contour rule tools/probes/ramp/ramp_engine.py's glow() uses to place its rim), so
recompositing data/icon_ramp/rims.json's rim over one of these bodies reproduces the shipped
texture pixel-exact -- the reconstruction identity proven in tools/probes/lw247_repro_census.py
and lw247_repro_census2.py.

Regenerate with `python tools/probes/lw247_extract_bodies.py`; do not hand-edit these PNGs.

Environment pin: Python 3.13.6, Pillow 12.0.0,
FF16Tools.CLI 1.13.2 (directory-name version; not invoked by this script, which only reads
already-decoded vanilla_cache DDS and banked PNGs).

| file | id | surface | family | banked source round | census2 verdict |
|---|---|---|---|---|---|
| ei_130_uitx.png | 130 | card | shield | shield_flashy_2026-08-16/complement_soft_bake | BODY-VEND |
| ei_132_uitx.png | 132 | card | shield | shield_flashy_2026-08-16/complement_soft_bake | BODY-VEND |
| ei_134_uitx.png | 134 | card | shield | shield_flashy_2026-08-16/complement_soft_bake | BODY-VEND |
| ei_136_uitx.png | 136 | card | shield | shield_flashy_2026-08-16/complement_soft_bake | BODY-VEND |
| ei_138_uitx.png | 138 | card | shield | shield_flashy_2026-08-16/complement_soft_bake | BODY-VEND |
| ei_140_uitx.png | 140 | card | shield | shield_flashy_2026-08-16/complement_soft_bake | BODY-VEND |
| ei_145_uitx.png | 145 | card | helm | session_bank_2026-08-16/bake_work | BODY-VEND |
| ei_149_uitx.png | 149 | card | helm | session_bank_2026-08-16/bake_work | BODY-VEND |
| ei_150_uitx.png | 150 | card | helm | session_bank_2026-08-16/bake_work | BODY-VEND |
| ei_151_uitx.png | 151 | card | helm | session_bank_2026-08-16/bake_work | BODY-VEND |
| ei_152_uitx.png | 152 | card | helm | session_bank_2026-08-16/bake_work | BODY-VEND |
| ei_154_uitx.png | 154 | card | helm | session_bank_2026-08-16/bake_work | BODY-VEND |
| ei_s_150_uitx.png | 150 | small | helm | session_bank_2026-08-16/bake_work | BODY-VEND |
| ei_s_151_uitx.png | 151 | small | helm | session_bank_2026-08-16/bake_work | BODY-VEND |
| ei_s_152_uitx.png | 152 | small | helm | session_bank_2026-08-16/bake_work | BODY-VEND |
| ei_s_154_uitx.png | 154 | small | helm | session_bank_2026-08-16/bake_work | BODY-VEND |

16 files extracted.
