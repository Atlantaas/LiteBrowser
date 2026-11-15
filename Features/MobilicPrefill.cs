using System.IO;
using System.Reflection;

namespace LiteCore
{
    /// Pré-remplit le formulaire « mission passée » sur mobilic.beta.gouv.fr.
    /// Script injecté dans chaque onglet ; il ne fait rien hors de ce site (cadre principal).
    /// Aucune connexion, aucun appel API, le bouton Continuer n'est jamais cliqué.
    public static class MobilicPrefill
    {
        static string script;

        public static string Script => script ??= Load();

        static string Load()
        {
            var asm = Assembly.GetExecutingAssembly();
            using var res = asm.GetManifestResourceStream("mobilic-prefill.js");
            if (res == null) return "(() => {})()";
            using var rd = new StreamReader(res);
            return rd.ReadToEnd();
        }
    }
}
