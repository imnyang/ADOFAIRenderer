# ADOFAI Renderer

<div dir="rtl">

ADOFAI Renderer هو تعديل يعمل مع Unity Mod Manager لتحويل المراحل المخصصة في ADOFAI إلى ملفات MP4 بدقة ومعدل إطارات محددين.

## الميزات

- اضغط `F6` لتسجيل المرحلة المخصصة المفتوحة حالياً.
- نافذة تقدم في المنتصف تعرض FPS والسرعة النسبية وETA ووقت الانتهاء المتوقع.
- إعدادات Preview وFullHD وQHD وUHD 4K وCustom.
- إمكانية ضبط الدقة و15–240 FPS ومعدل البت 1–200 Mbps ووقت الانتظار النهائي والصوت ومجلد الإخراج.
- اختيار NVENC تلقائياً على بطاقات NVIDIA مع بديل برمجي هو `libx264`.
- تسجيل صوت اللعبة اختيارياً ثم دمجه مع الفيديو.
- وضع BGA يخفي البلاطات وHold والمؤثرات والكواكب وجزيئات الكواكب وأصوات الضرب الخاصة باللعب.
- يدعم RPC المحلي خيار `bgaMode` لكل مهمة.

## التثبيت

بعد تنزيل التعديل، ضعه بشكل مرتب داخل مجلد `Mods` الخاص باللعبة:

```text
A Dance of Fire and Ice/Mods/ADOFAIRenderer/
```

ضع `ADOFAIRenderer.dll` و`Info.json` في مجلد التعديل. عند التشغيل لأول مرة، ينزّل التعديل تلقائياً ملف FFmpeg المناسب للمنصة إلى `FFmpeg/<platform>/`، مع احترام أي تثبيت موجود أو مسار محدد في إعداد `FFmpeg executable`. فعّل التعديل من Unity Mod Manager، وافتح مرحلة مخصصة، واضبط الإعدادات، ثم اضغط `F6`.

يدعم التشغيل Windows وmacOS وLinux عندما تتوفر نسخة متوافقة من ADOFAI وUnity Mod Manager.

لا يتضمن ناتج البناء FFmpeg. عند تشغيل التعديل، يختار تلقائياً وينزّل نسخة واحدة فقط: `windows-x64` أو `linux-x64` أو `macos-x64` أو `macos-arm64` لأجهزة Apple Silicon.

## التحديث التلقائي

يتحقق التعديل عند التشغيل من أحدث إصدار رسمي على GitHub. يتم استبعاد إصدارات Draft وPre-release باستخدام `releases/latest`، ثم يتم التحقق من إصدار ZIP وتجزئة SHA-256. عند عدم وجود عملية رندر نشطة، يتم تطبيق التحديث عبر hot-reload في Unity Mod Manager دون إعادة تشغيل اللعبة؛ وإذا تعذر ذلك، يتم تطبيقه بعد إغلاق اللعبة.

## الإعدادات

| الإعداد | القيمة الافتراضية | الوصف |
| --- | --- | --- |
| Preset | FullHD | Preview / FullHD / QHD / UHD 4K / Custom |
| Width / Height | 1920 × 1080 | دقة مخصصة، ويتم تصحيحها إلى أرقام زوجية |
| Target FPS | 60 | من 15 إلى 240 |
| Video bitrate | 18 Mbps | CBR من 1 إلى 200 Mbps |
| End delay | ثانيتان | الانتظار بعد الموسيقى أو آخر بلاطة |
| Capture audio | مفعّل | تسجيل صوت اللعبة |
| BGA mode | متوقف | بدون البلاطات والكواكب وأصوات الضرب |
| Encoding speed | Quality | Maximum / Balanced / Quality |
| Video encoder | Auto | Auto / NvidiaNvenc / Software |
| Output folder | `Renders` | مسار نسبي من مجلد اللعبة أو مسار مطلق |

### وضع BGA

يحفظ BGA Mode حالة العارض الأصلية، ويخفي عناصر اللعب قبل رسم الكاميرا، ويتخطى جدولة أصوات الضرب، ثم يعيد الحالة الأصلية بعد النجاح أو الإلغاء أو الفشل. تبقى الخلفية والكاميرا والزخارف وتوقيت الموسيقى كما هي. لإزالة الموسيقى أيضاً عطّل `Capture audio`.

## RPC

شغّل اللعبة مع الخيار:

```text
--renderer-rpc
```

العنوان الافتراضي هو `http://127.0.0.1:1108/`. راجع [مواصفات RPC API](docs/RPC_API.md) لمعرفة المسارات وبنية الطلبات والاستجابات ورموز الحالة وأمثلة JavaScript.

```js
const job = await fetch('http://127.0.0.1:1108/render', {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({
    levelPath: 'C:/Levels/MyLevel.adofai',
    preset: 'FullHD',
    bgaMode: true,
    captureAudio: true
  })
}).then(response => response.json());

console.log(job);
```

## البناء والاختبار

```powershell
.\build.ps1 -Test
```

استخدم `-GameDir` لمسار لعبة غير افتراضي و`-MSBuildPath` لتحديد MSBuild.

## الترخيص

كود المشروع مرخص بموجب MIT License في [ADOFAIRenderer/LICENSE.md](ADOFAIRenderer/LICENSE.md)، بينما تخضع ADOFAI وUnity وUnity Mod Manager وFFmpeg لتراخيصها الخاصة.

</div>
