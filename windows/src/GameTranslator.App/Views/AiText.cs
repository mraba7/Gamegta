// SPDX-License-Identifier: AGPL-3.0-only

using GameTranslator.Core;

namespace GameTranslator.App.Views;

/// <summary>
/// Arabic wording of the AI translation cards (Arabic edition). The rest of the Windows UI is still the original
/// app's; these are the texts an Arabic user has to read to choose a service, enter a key and pick a quality mode.
/// </summary>
internal static class AiText
{
    public const string KeyNeededTitle = "مطلوب مفتاح API";
    public const string KeyMissingTitle = "المفتاح ناقص";
    public const string KeyAcceptedTitle = "تم التفعيل";
    public const string KeyRejectedTitle = "المفتاح لم يُقبل";

    public const string LocalTitle = "ترجمة Google المجانية (بدون مفتاح)";
    public const string LocalDescription =
        "تعمل بدون مفتاح وبدون تكلفة، لكن جودتها أقل من خدمات الذكاء الاصطناعي وترجمتها أقرب للحرفية. تُستخدم تلقائياً إذا تعذّر الوصول للخدمة المختارة.";
    public const string GeminiDescription =
        "ترجمة بالذكاء الاصطناعي من Google. المودل السريع يعمل بمفتاح مجاني، وأقوى مودل يحتاج حساباً مدفوعاً.";

    public static string KeyNeeded(AiProvider provider) =>
        $"لاستخدام {provider.DisplayName()} أدخل مفتاح API في خانته ثم اضغط «حفظ وتفعيل».";

    public static string KeyMissing(AiProvider provider) => $"أدخل مفتاح {provider.DisplayName()} أولاً.";

    public static string KeyAccepted(AiProvider provider) =>
        $"تم قبول مفتاح {provider.DisplayName()} وصار هو خدمة الترجمة الحالية. أرسلنا ترجمة تجريبية للتلفزيون.";

    public static string KeyRejected(AiProvider provider) =>
        $"لم تقبل {provider.DisplayName()} هذا المفتاح، أو تعذّر الاتصال بها. تأكد من المفتاح ومن الإنترنت ثم حاول مرة أخرى.";

    /// <summary>Says which model the chosen mode uses, so the user knows what they pay for and why a line may be slow.</summary>
    public static string QualityHint(AiProvider? active, QualityMode mode)
    {
        var what = mode == QualityMode.Best
            ? "أقوى مودل عند الخدمة: أفضل ترجمة، لكنه أبطأ وأغلى."
            : "المودل السريع: يظهر النص أسرع وبتكلفة أقل، والجودة أقل قليلاً.";
        return active is { } provider ? $"{what} المودل المستخدم: {provider.ModelFor(mode).Id}" : what + " اختر خدمة ذكاء اصطناعي من الأسفل ليُطبَّق الوضع.";
    }

    public static string DialectHint(ArabicStyle style) => style switch
    {
        ArabicStyle.Colloquial => "عامية بسيطة يفهمها الجميع، أقرب لكلام الناس من الفصحى.",
        ArabicStyle.Saudi => "لهجة سعودية يومية. تناسب كلام الشوارع والمزح أكثر.",
        _ => "فصحى سهلة مثل ترجمة الأفلام. الأوضح والأثبت.",
    } + " كل أسلوب له ذاكرة ترجمة خاصة به. ويعمل مع خدمات الذكاء الاصطناعي فقط.";

    /// <summary>Why the AI service is not translating right now, and what the user can do about it.</summary>
    public static string Trouble(OnlineKeyState state) => state switch
    {
        OnlineKeyState.Invalid => "⚠ الخدمة رفضت المفتاح. الترجمة الآن بالترجمة المجانية (جودة أقل). تأكد من المفتاح في الأسفل.",
        OnlineKeyState.QuotaExceeded => "⚠ انتهى الرصيد أو الحد المسموح في حسابك عند الخدمة. الترجمة الآن بالترجمة المجانية (جودة أقل).",
        OnlineKeyState.ModelRejected => "⚠ الخدمة لم تقبل هذا المودل بهذا المفتاح. جرّب وضع «أسرع» أو خدمة أخرى. الترجمة الآن بالترجمة المجانية.",
        _ => "⚠ خدمة الترجمة لا ترد الآن (أو ردّها تأخر). الترجمة مؤقتاً بالترجمة المجانية (جودة أقل) وتعود تلقائياً عند رجوع الخدمة.",
    };
}
