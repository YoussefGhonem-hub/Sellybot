// What's new in Sellybot. Store owners see it after an update, in the dashboard and in the desktop app.
//
// Every new feature an owner can see: add an entry AT THE TOP, with an id that was never used before (the date + a word).
//   icon · title · text: one line · items: details (optional)
//   link (optional): { tab: "look", label: "جرّبه" } opens a dashboard tab · { href: "download.html", label: "..." } opens a page
//
// How it reaches them:
// - An open dashboard (or the desktop app) re-reads this file every 10 minutes and whenever its window comes back to the front.
//   A new id at the top shows the bar "✨ جديد في Sellybot" with a reload button; in the desktop app also a Windows notification.
// - After loading, the entries this browser hasn't seen yet open once in the "الجديد في Sellybot" window.
window.SB_NEWS = [
  {
    id: "2026-11-15-desktop", date: "2026-11-15", icon: "🖥️",
    title: "Sellybot على الكمبيوتر",
    text: "لوحة تحكم متجرك بقت برنامج لوحده على Windows، من غير متصفح.",
    items: ["أيقونة على سطح المكتب، ودوسة واحدة وتبقى في لوحتك", "بيفتكر تسجيل دخولك، وبيتحدّث لوحده", "مجاني في كل الباقات"],
    link: { href: "download.html", label: "حمّل البرنامج" }
  },
  {
    id: "2026-11-15-monkey", date: "2026-11-15", icon: "🐒",
    title: "شكل جديد للشات: القرد المبرمج",
    text: "بيسمع مزيكا وبيسلّم على زباينك، وسماعاته بتاخد لون براندك.",
    link: { tab: "look", label: "جرّبه على متجرك" }
  },
  {
    id: "2026-11-15-replies", date: "2026-11-15", icon: "💬",
    title: "ردود أقصر وأقرب للبشر",
    text: "المساعد بقى يرد في سطر أو اتنين، من غير كلام رسمي ولا آلي.",
    items: ["على EasyOrders: اللي عايز يطلب بيوصله لينك المنتج على طول", "على Shopify: بيطلب بيانات التوصيل كلها في رسالة واحدة"]
  }
];
