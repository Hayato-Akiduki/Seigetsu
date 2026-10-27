using Velopack;

namespace Seigetsu;

/// <summary>
/// アプリの入り口。Velopack（インストールと自動更新）は、ほかの何よりも先に動かす必要がある。
/// インストール・更新・アンインストールのときは、Velopack がここで処理をしてすぐ終わることがある。
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
