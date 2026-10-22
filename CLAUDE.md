# ThinkJetGpib: notes for Claude

Windows Unidrv (GPD) driver plus a GPIB print bridge service for an HP 2225A ThinkJet. See README.md
for the architecture. Build: `dotnet test tests\ThinkJetGpib.Tests`, then `dotnet publish src\ThinkJetGpib -c Release -o dist`.

## ThinkJet facts (from HP manuals, not guesses)

Sources: the owner's OneDrive, "OneDrive - The Schnauzer Group\PCL": ThinkJet Service Manual 02225-90079
(Nov 86), 2225A Reference Manual 02225-90007 (Jan 84), and the PCL Implementor's Guide.
In the Reference Manual, the graphics pages (3-3 to 3-5) and Appendix A are scanned images with no
text layer.

- Raster commands (Service Manual Table 10-3, p. 10-4): `ESC*r640S` low density (640 dots per line,
  96 x 96 dpi), `ESC*r1280S` high density (1280 dots per line, 192 x 96 dpi), `ESC*rA` start raster,
  `ESC*b#W` one dot row, `ESC*rB` end raster. The ThinkJet speaks PCL Level I.
- Specs (Service Manual p. 1-6): printable width 6.7 in; dot spacing 96 x 96 or 96 x 192 dpi;
  **dot size 0.007 in**; buffer 1 KB. At 96 dpi (pitch 0.0104 in) adjacent dots never touch.
- The Reference Manual (p. 1-17) says the printer is designed for about 1 in side margins on 8.5 in
  paper.

## Print-quality work (October 2026)

A printed E4419B test-record PDF scanned at 600 dpi showed three problems, each tracked as an issue:

- **#1 Clipping, left and right.** The print line is 6.67 in, not 8 in as the old comments said.
  The driver geometry is right; documents with margins under about 0.92 in get cropped. The fix is
  documentation plus "Fit to printable area". A frame test to fine-tune the origin is still to do.
- **#2 Coloured and grey content is dithered** into faint dots: blue headings, green PASS, grey
  7 pt captions, grey header fills. Idea: print text and thin lines as solid black, and drop light
  fills to white (a GPD option, or an OEMUNI plug-in as a fallback). Not started. Still unknown:
  which app the PDF was printed from.
- **#3 Add 192 x 96 dpi.** Done in PR #4 (branch `claude/zen-pascal-uxvus2`, draft):
  `MasterUnits` 192 x 96, a new default resolution "192 x 96" that sends `ESC*r1280S` (96 x 96
  sends `ESC*r640S`), and `ThinkJetGpib.exe --density-test`. Unit tests pass. **Not yet tested
  on hardware.**

## Next steps on the bench

1. Stop the service and run `ThinkJetGpib.exe --density-test`. With a loupe, check the 192 dpi
   solid and 1-on/1-off bars. Open question: can the head fire horizontally adjacent dots in one
   pass at 192 dpi?
2. Install the PR build, reprint the E4419B record with "Fit to printable area" and compare it with
   the earlier scan. If it looks good, take PR #4 out of draft. Then do #2.
