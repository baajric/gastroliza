using ApexCharts;

namespace Analitika.Server.Servisi;

/// <summary>
/// Zajednički izgled svih grafova: tanke linije, mreža povučena u pozadinu,
/// bez broja na svakoj tački. Svi grafovi izgledaju kao jedan sistem.
/// </summary>
public static class GrafStil
{
    /// <param name="saPoredjenjem">
    /// Kad se crtaju dvije serije, druga je isprekidana i bez ispune — boja sama
    /// ne bi bila dovoljna da se odmah vidi koja je linija prošli period.
    /// </param>
    public static ApexChartOptions<T> Linijski<T>(bool saPoredjenjem = false) where T : class
    {
        var o = Osnova<T>();

        // Boje linija se moraju zadati i ovdje: kod linijskih serija ApexCharts uzima
        // boju poteza iz Stroke, pa je druga serija bez ovoga ispadala siva.
        o.Stroke = saPoredjenjem
            ? new Stroke
            {
                Width = [2, 1.5],
                Curve = Curve.Smooth,
                DashArray = [0, 4],
                Colors = [Paleta.Serija1(), Paleta.Serija2()]
            }
            : new Stroke
            {
                Width = 2,
                Curve = Curve.Smooth,
                Colors = [Paleta.Serija1()]
            };

        // Legenda uzima boje iz Colors, ne iz Stroke — bez ovoga bi prethodni period
        // u legendi dobio boju druge kategorije umjesto sive.
        o.Colors = saPoredjenjem
            ? [Paleta.Serija1(), Paleta.Serija2()]
            : [Paleta.Serija1()];

        // Gradijent ispune ApexCharts primjenjuje i na potez linije, pa je druga serija
        // dobijala izblijedjelu prvu boju umjesto svoje. Kod poređenja se ispuna izostavlja:
        // dvije ispunjene površine bi ionako zaklanjale jedna drugu.
        if (!saPoredjenjem)
        {
            o.Fill = new Fill
            {
                Type = [FillType.Gradient],
                Gradient = new FillGradient { OpacityFrom = 0.22d, OpacityTo = 0d, ShadeIntensity = 0d }
            };
        }

        // Obje vrijednosti u istom oblačiću — poređenje se čita odjednom, bez lutanja mišem.
        o.Tooltip = new Tooltip
        {
            Enabled = true,
            Shared = saPoredjenjem,
            Intersect = false,
            Theme = Mode.Dark
        };
        // Na dugom periodu bi svaki dan dobio oznaku i osa bi postala siva traka.
        // Desetak ravnomjernih oznaka je dovoljno — tačan dan je u oblačiću.
        o.Xaxis.TickAmount = 10;
        o.Xaxis.Labels!.Rotate = 0;
        o.Xaxis.Labels.HideOverlappingLabels = true;
        o.Xaxis.AxisTicks = new AxisTicks { Show = false };

        // Tačke se ne crtaju stalno; 2px prsten u boji površine ih odvaja pri prelasku mišem.
        o.Markers = new Markers { Size = 0, StrokeWidth = 2, StrokeColors = Paleta.Povrsina() };
        return o;
    }

    public static ApexChartOptions<T> Stupci<T>() where T : class
    {
        var o = Osnova<T>();
        o.PlotOptions = new PlotOptions
        {
            Bar = new PlotOptionsBar
            {
                ColumnWidth = "70%",
                // Blago zaobljen vrh stupca, kao u Square i DoorDash pregledima prodaje.
                BorderRadius = 3,
                BorderRadiusApplication = BorderRadiusApplication.End
            }
        };
        return o;
    }

    public static ApexChartOptions<T> Trake<T>() where T : class
    {
        var o = Osnova<T>();
        o.PlotOptions = new PlotOptions
        {
            Bar = new PlotOptionsBar
            {
                Horizontal = true,
                BarHeight = "65%",
                BorderRadius = 4,
                BorderRadiusApplication = BorderRadiusApplication.End
            }
        };

        // Kod vodoravnih traka ose zamjene uloge: vrijednost je na X, nazivi kategorija na Y.
        // Zato oblikovanje broja seli na X, a sa Y se skida — inače bi "Coca cola" postala NaN.
        o.Xaxis.Labels!.Formatter = Oblikovanje;
        o.Yaxis[0].Labels!.Formatter = null;
        o.Yaxis[0].Min = null;
        o.Xaxis.Min = 0;
        return o;
    }

    /// <summary>de-DE daje tačku kao razdjelnik hiljada, kako se i piše kod nas.</summary>
    private const string Oblikovanje =
        "function (v) { return v == null ? '' : Number(v).toLocaleString('de-DE', {maximumFractionDigits: 0}); }";

    /// <summary>Oznake na osama su monospace — brojevi se poravnaju i čitaju kao instrument.</summary>
    private const string Mono = "ui-monospace, 'Cascadia Mono', Consolas, monospace";

    private static ApexChartOptions<T> Osnova<T>() where T : class => new()
    {
        Chart = new Chart
        {
            Toolbar = new Toolbar { Show = false },
            Zoom = new Zoom { Enabled = false },
            FontFamily = "Inter, system-ui, -apple-system, 'Segoe UI', sans-serif",
            Background = "transparent",
            Animations = new Animations { Enabled = false }
        },
        Theme = new Theme { Mode = Mode.Dark },
        Colors = [.. Paleta.Kategorijski()],

        // Broj na svakoj tački je nečitljiv — vrijednosti nose osa, tooltip i tabela.
        DataLabels = new DataLabels { Enabled = false },

        Grid = new Grid
        {
            BorderColor = Paleta.Mreza(),
            StrokeDashArray = 0,   // pune tanke linije, nikad isprekidane
            Xaxis = new GridXAxis { Lines = new Lines { Show = false } },
            Padding = new Padding { Left = 4, Right = 8, Top = -8 }
        },
        Xaxis = new XAxis
        {
            // Kategorijska, ne brojčana: sa dvije serije zajednički oblačić spaja vrijednosti
            // po rednom mjestu. Na brojčanoj osi bi pokazivao samo jednu.
            Type = XAxisType.Category,
            AxisBorder = new AxisBorder { Color = Paleta.Osa() },
            AxisTicks = new AxisTicks { Color = Paleta.Osa() },
            Labels = new XAxisLabels { Style = new AxisLabelStyle { Colors = Paleta.PrigusenoMastilo(), FontSize = "11px", FontFamily = Mono } }
        },
        Yaxis =
        [
            new YAxis
            {
                // Vrijednosna osa uvijek kreće od nule — odsječena osa preuveličava razlike u prometu.
                // Kod vodoravnih traka ovo se poništava jer je Y tada osa kategorija.
                Min = 0,
                Labels = new YAxisLabels
                {
                    Style = new AxisLabelStyle { Colors = Paleta.PrigusenoMastilo(), FontSize = "11px", FontFamily = Mono },
                    Formatter = Oblikovanje
                }
            }
        ],
        Tooltip = new Tooltip { Enabled = true, Theme = Mode.Dark },
        Legend = new Legend { Show = false }
    };
}
