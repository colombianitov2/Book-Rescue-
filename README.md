# BookRescue

Aplicacion WPF en .NET 10 para rescatar libros antiguos escaneados como foto.

## Ejecutar

Desde PowerShell:

```powershell
cd "D:\Proyectos de desarrollo de Software\BookRescue\_migrado\Extractor de texto\BookRescue"
dotnet run --project ".\BookRescue.App\BookRescue.App.csproj"
```

Ejecutable compilado:

```text
D:\Proyectos de desarrollo de Software\BookRescue\_migrado\Extractor de texto\BookRescue\BookRescue.App\bin\Debug\net10.0-windows\BookRescue.App.exe
```

Para publicar una version Release self-contained:

```powershell
D:\Proyectos de desarrollo de Software\BookRescue\_migrado\Extractor de texto\BookRescue\publish-app.ps1
```

La salida publicada queda en el instalador portable:

```text
D:\Proyectos de desarrollo de Software\BookRescue\_migrado\Extractor de texto\BookRescue\installer
```

## Pruebas por consola

La app tambien permite ejecutar conversiones controladas sin abrir la interfaz:

```powershell
D:\Proyectos de desarrollo de Software\BookRescue\_migrado\Extractor de texto\BookRescue\BookRescue.App\bin\Release\net10.0-windows\BookRescue.App.exe `
  --convert "D:\ruta\entrada.pdf" `
  --out "D:\ruta\salida" `
  --ocr eng `
  --lang es `
  --mode heavy `
  --formats pdf,word,csv `
  --no-translate
```

Modos aceptados por `--mode`:

- `text`: Extraer solo texto.
- `photos`: Texto y fotos.
- `heavy`: Reconstrucción perfecta pesada.

En modo `heavy`, `MaximumQuality` se activa internamente y la IA local se usa automaticamente salvo que se indique `--no-ai`.

## Que hace

- Sube uno o varios PDF/imagenes.
- Permite elegir la carpeta destino.
- Convierte PDF a paginas de imagen de alta resolucion.
- Restaura contraste, ruido y nitidez con OpenCV.
- Ejecuta OCR principal con Tesseract.
- Detecta automaticamente el idioma del texto.
- Genera PDF, Word, ePub y CSV segun el perfil elegido.
- Mantiene una biblioteca persistente en `%LOCALAPPDATA%\BookRescue\library.json`.
- Oculta OCR, traductor y dependencias tecnicas; la app las prepara automaticamente.
- La traduccion queda desactivada por defecto; si el usuario la activa, intenta traducir hacia el idioma de salida.
- Permite elegir tres modos de reconstruccion: Extraer solo texto, Texto y fotos o Reconstrucción perfecta pesada.
- Incluye boton de actualizacion para consultar el repositorio oficial cuando se publiquen versiones.

## Modos de reconstruccion

### Extraer solo texto

Extrae texto limpio y organizado. No inserta portada, fotos, figuras ni recortes visuales en las salidas finales. Si se seleccionan PDF, Word, ePub o CSV, esos archivos se generan con el sufijo `_solo_texto`.

### Texto y fotos

Modo por defecto. Extrae texto y fotos, y organiza el documento con fuentes predeterminadas, estilo de libro, titulos, subtitulos, parrafos, tablas, formulas sencillas y figuras rescatadas. Las salidas se generan con el sufijo `_texto_y_fotos`.

### Reconstrucción perfecta pesada

Modo avanzado y lento. Intenta reconstruir por capas: fondo limpio, recortes regionales, decoracion editorial simple, tablas, figuras y texto OCR visible. Tambien genera un reporte JSON detallado por pagina con regiones detectadas, texto OCR asociado, confianza, decisiones de preservacion y metrica de similitud visual.

Advertencia en la app:

```text
Este modo puede tardar mucho más y usar más CPU/GPU.
```

La antigua opcion de maxima calidad no aparece como boton ni selector. `MaximumQuality` se activa internamente solo cuando el modo seleccionado es `Reconstrucción perfecta pesada`.

## IA y OCR

- Tesseract es el OCR principal de la aplicacion y se usa en todos los modos.
- OpenHermes/Mistral trabaja solamente con texto OCR. No recibe imagenes y no es un modelo de vision.
- Docling, RapidOCR, PP-OCRv4, TableFormer y CodeFormula ayudan al analisis documental del modo `Reconstrucción perfecta pesada`.
- OpenCV se usa para restauracion visual, deteccion de regiones, recorte de figuras/tablas y clasificacion basica de daño del escaneo.
- Las grietas, manchas, ruido y suciedad se tratan como daño del escaneo, no como decoracion editorial.

## Idiomas

La app prepara los paquetes OCR necesarios en segundo plano. Internamente puede usar codigos Tesseract como:

```text
spa+eng
```

Los archivos `.traineddata` incluidos o descargados se ubican en `runtime\tessdata` o en `%LOCALAPPDATA%\BookRescue\tessdata`.

## Traduccion ingles a espanol

La salida por defecto es `es`. La app detecta el idioma y, si `Traducir automaticamente` esta activo, intenta traducir hacia el idioma de salida.

La traduccion usa una API local compatible con LibreTranslate. Por defecto busca:

```text
http://localhost:5000
```

Si el paquete offline esta instalado, la app inicia sola el traductor local cuando hace falta. El instalador incluye modelos Argos `en -> es` y `es -> en`.

## Modo offline incluido

La app no necesita internet, Docker, Python externo ni Microsoft Word para traducir o generar salidas en el paquete final. El instalador incluye:

- runtime .NET self-contained;
- OCR `eng`, `spa` y `osd`;
- traductor local congelado `BookRescueTranslator.exe`;
- modelos Argos `en -> es` y `es -> en`;
- modelos MiniSBD para segmentacion de frases;
- OpenXML para generar Word `.docx`;
- Typst para composicion PDF cientifica cuando aplica;
- modelos Docling/RapidOCR para el modo pesado.

Ejecuta el instalador local:

```powershell
D:\Proyectos de desarrollo de Software\BookRescue\_migrado\Extractor de texto\BookRescue\installer\Install-BookRescue.ps1
```

O ejecuta portable sin instalar:

```powershell
D:\Proyectos de desarrollo de Software\BookRescue\_migrado\Extractor de texto\BookRescue\installer\Run-Portable-BookRescue.ps1
```

## Salidas

Cada libro convertido crea una carpeta con el nombre del libro y fecha/hora.

### Extraer solo texto

- `*_solo_texto.txt`
- `*_solo_texto.pdf` si PDF esta seleccionado
- `*_solo_texto.docx` si Word esta seleccionado
- `*_solo_texto.epub` si ePub esta seleccionado
- `*_solo_texto.csv` si CSV esta seleccionado

### Texto y fotos

- `*_texto_y_fotos.txt`
- `*_texto_y_fotos.pdf` si PDF esta seleccionado
- `*_texto_y_fotos.docx` si Word esta seleccionado
- `*_texto_y_fotos.epub` si ePub esta seleccionado
- `*_texto_y_fotos.csv` si CSV esta seleccionado
- `imagenes_rescatadas`

### Reconstrucción perfecta pesada

- `*_reconstruccion_perfecta_pesada.txt`
- `*_reconstruccion_perfecta_pesada.pdf` si PDF esta seleccionado
- `*_reconstruccion_perfecta_pesada.docx` si Word esta seleccionado
- `*_reconstruccion_perfecta_pesada.epub` si ePub esta seleccionado
- `*_reconstruccion_perfecta_pesada.csv` si CSV esta seleccionado
- `*_reporte_reconstruccion_perfecta_pesada.json`
- `fallback_visual_regional` si existen recortes de respaldo
- `comparacion_visual_reconstruida` con vistas PNG usadas para la metrica de similitud del reporte

## Biblioteca local

La biblioteca se guarda en:

```text
%LOCALAPPDATA%\BookRescue\library.json
```

Cada registro conserva nombre del libro, ruta original, carpeta de salida, modo usado, rutas de TXT/PDF/DOCX/ePub/CSV, ruta del reporte pesado si existe, fecha de conversion y estado.

## Repos revisados en `D:\Repos`

- `LibreTranslate-main.zip`: util. Ya se uso como base del traductor local congelado.
- `itext-dotnet-develop.zip`: util como referencia, pero la app usa el paquete NuGet `itext` 9.6.0.
- `tesseract-master.zip`: util como referencia, pero no se compila porque la app usa `Tesseract` NuGet y paquetes `.traineddata`.
- `PdfiumViewer-master.zip`: revisado, pero no se usa porque es mas natural para .NET Framework/WinForms; en .NET 10 usamos `PDFtoImage`.
- `PdfPig-master.zip`: queda como candidato futuro para extraer texto embebido de PDFs no escaneados.
- `English-to-Spanish-Translation-APP-main.zip` y `EasyNMT-main.zip`: no se integran ahora porque harian mas pesado el paquete que Argos/LibreTranslate y no mejoran la instalacion offline actual.
- `MinerU-master.zip`: candidato futuro para analisis de layout avanzado, pero es demasiado grande/complejo para el instalador base.

## Paquetes usados

- iText 9.6.0 para escribir PDF reconstruido.
- PDFtoImage 5.2.1 para renderizar PDF en .NET 10.
- Tesseract 5.2.0 para OCR.
- OpenCvSharp para restauracion visual y analisis de regiones.
- DocumentFormat.OpenXml 3.3.0 para generar Word sin Office instalado.
- CommunityToolkit.Mvvm para MVVM.
