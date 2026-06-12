# Runtime offline

Este repositorio guarda el codigo fuente de BookRescue, no el paquete pesado de ejecucion offline.

Por tamano y rendimiento de GitHub, estas carpetas no se versionan en Git:

- `runtime/`
- `installer/`
- `publish/`
- `verification/`
- `smoke-test/`
- salidas `*-output/`

La beta offline completa incluye modelos IA, Docling, OCR, traductor, Python portable, Typst y LibreOffice. Ese paquete debe distribuirse como release o archivo externo, no como contenido del repositorio.

Para desarrollo local, conserva el runtime offline junto al proyecto o genera el instalador con:

```powershell
.\publish-app.ps1
```

La version portable actual se mantiene localmente en:

```text
D:\Proyectos de desarrollo de Software\BookRescue
```
