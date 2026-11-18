# PawnIO redistribution review
Checked 2026-11-18 against upstream:
- https://github.com/namazso/PawnIO (README.md): GPL-2.0-or-later with a special exception for LGPL software and independent modules communicating solely through device IOCTL. This is not an unconditional exemption for every Pawn module or linked component.
- https://github.com/namazso/PawnIO/blob/master/COPYING contains GPL v2.
- https://pawnio.eu states the official edition is identical to open source but digitally signed.
- https://github.com/namazso/PawnIO.Setup contains official releases and only a README; no separate installer license was found there.
The bundled installer is unchanged official 2.2.0, SHA256 1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032.
Before public redistribution, resolve complete corresponding source/offer for this exact release and installer components; a link to current master alone does not establish this. This local candidate is not cleared for public redistribution by this review. Do not infer a proprietary redistribution permission from a downloadable EXE.
PanelOS installations and public publication are separate actions; no installer is executed by packaging.
