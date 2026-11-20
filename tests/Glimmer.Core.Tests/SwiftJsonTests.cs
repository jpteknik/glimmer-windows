// Not a port: checks that what the Mac's Codable types write reads here, and that what is written here has the shapes
// the Mac reads. The JSON below is written by hand in the form Swift's JSONEncoder([.prettyPrinted, .sortedKeys])
// produces (" : " between key and value, "\/" for "/"), from the Swift sources' Codable conformances.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glimmer.Core.Tests;

public class SwiftJsonTests
{
    private const string MacManifest = """
    {
      "activeLayerID" : "6F2A1C9E-7B0D-4E61-9C5A-2D8E4F1B3A70",
      "aids" : {
        "grid" : {
          "isVisible" : true,
          "spacing" : 50,
          "subdivisions" : 2
        },
        "guides" : [
          {
            "axis" : "vertical",
            "id" : "0E0F1A2B-3C4D-4E5F-8061-728394A5B6C7",
            "position" : 120.5
          }
        ],
        "rulersVisible" : true,
        "snap" : false
      },
      "blendSpace" : "gamma",
      "colorSpace" : "displayP3",
      "formatVersion" : 1,
      "height" : 600,
      "importReport" : {
        "flattened" : false,
        "groupsKept" : 1,
        "layersInFile" : 5,
        "layersKept" : 4,
        "meanDifference" : 0.4,
        "notes" : [
          {
            "changesLook" : true,
            "detail" : "Gradient",
            "kind" : "fillLeftOut",
            "layer" : "Sky"
          }
        ],
        "shareVisiblyDifferent" : 0.0005,
        "source" : "RGB, 8 bits per channel"
      },
      "layers" : [
        {
          "blendMode" : "normal",
          "id" : "11111111-2222-4333-8444-555555555555",
          "kind" : "raster",
          "mask" : {
            "defaultValue" : 0,
            "density" : 0.7,
            "enabled" : true,
            "feather" : 3
          },
          "name" : "Bakgrund åäö",
          "opacity" : 1,
          "raw" : {
            "byteCount" : 3,
            "fileName" : "IMG_0001.DNG",
            "settings" : {
              "exposure" : 0.5,
              "highlightRecovery" : true,
              "process" : "9",
              "shadows" : 1.2,
              "temperature" : 5600
            }
          },
          "visible" : true
        },
        {
          "adjustment" : {
            "colors" : {
              "color" : {
                "b" : 0.2,
                "g" : 0.6,
                "r" : 0.92
              }
            },
            "curves" : [
              "red",
              [
                {
                  "x" : 0,
                  "y" : 0.1
                },
                {
                  "x" : 1,
                  "y" : 0.9
                }
              ],
              "rgb",
              [
                {
                  "x" : 0,
                  "y" : 0
                },
                {
                  "x" : 0.5,
                  "y" : 0.6
                },
                {
                  "x" : 1,
                  "y" : 1
                }
              ]
            ],
            "flags" : {
              "naturalSpline" : true
            },
            "gradient" : [
              {
                "color" : {
                  "b" : 0,
                  "g" : 0,
                  "r" : 0
                },
                "opacity" : 1,
                "position" : 0
              },
              {
                "color" : {
                  "b" : 1,
                  "g" : 1,
                  "r" : 1
                },
                "opacity" : 1,
                "position" : 1
              }
            ],
            "kind" : "curves",
            "levels" : [
              "blue",
              {
                "gamma" : 1.1,
                "inBlack" : 0.05,
                "inWhite" : 0.95,
                "outBlack" : 0,
                "outWhite" : 1
              }
            ],
            "numbers" : {
              "exposure" : 1.5
            }
          },
          "blendMode" : "soft-light",
          "clipped" : true,
          "effects" : {
            "dropShadow" : {
              "angle" : 120,
              "blendMode" : "multiply",
              "color" : {
                "b" : 0,
                "g" : 0,
                "r" : 0
              },
              "distance" : 10,
              "isEnabled" : true,
              "opacity" : 0.75,
              "size" : 10,
              "spread" : 0
            },
            "stroke" : {
              "blendMode" : "normal",
              "color" : {
                "b" : 0,
                "g" : 0,
                "r" : 1
              },
              "isEnabled" : false,
              "opacity" : 1,
              "position" : "center",
              "width" : 3
            }
          },
          "id" : "22222222-3333-4444-8555-666666666666",
          "kind" : "adjustment",
          "name" : "Curves",
          "opacity" : 0.8,
          "visible" : true
        },
        {
          "blendMode" : "pass-through",
          "children" : [
            {
              "blendMode" : "normal",
              "fill" : {
                "gradient" : {
                  "_0" : {
                    "end" : [
                      10,
                      20
                    ],
                    "reverse" : true,
                    "start" : [
                      0,
                      0
                    ],
                    "stops" : [
                      {
                        "color" : {
                          "b" : 0,
                          "g" : 0,
                          "r" : 0
                        },
                        "opacity" : 1,
                        "position" : 0
                      },
                      {
                        "color" : {
                          "b" : 1,
                          "g" : 1,
                          "r" : 1
                        },
                        "opacity" : 0.5,
                        "position" : 1
                      }
                    ],
                    "style" : "radial"
                  }
                }
              },
              "id" : "33333333-4444-4555-8666-777777777777",
              "kind" : "fill",
              "name" : "Gradient",
              "opacity" : 1,
              "vectorMask" : {
                "id" : "44444444-5555-4666-8777-888888888888",
                "name" : "Ellipse",
                "subpaths" : [
                  {
                    "anchors" : [
                      {
                        "handleIn" : [
                          0,
                          0
                        ],
                        "handleOut" : [
                          5,
                          0
                        ],
                        "point" : [
                          0,
                          0
                        ]
                      }
                    ],
                    "isClosed" : true
                  }
                ]
              },
              "visible" : false
            },
            {
              "blendMode" : "normal",
              "id" : "55555555-6666-4777-8888-999999999999",
              "kind" : "smart",
              "name" : "Placed",
              "opacity" : 1,
              "smart" : {
                "filters" : [
                  {
                    "id" : "66666666-7777-4888-8999-AAAAAAAAAAAA",
                    "isEnabled" : false,
                    "settings" : {
                      "kind" : "gaussian_blur",
                      "values" : {
                        "radius" : 6
                      }
                    }
                  }
                ],
                "height" : 100,
                "transform" : {
                  "a" : 1.5,
                  "b" : 0,
                  "c" : 0,
                  "d" : 1.5,
                  "g" : 0.001,
                  "h" : 0,
                  "tx" : 5,
                  "ty" : 6
                },
                "width" : 200
              },
              "visible" : true
            },
            {
              "blendMode" : "normal",
              "id" : "6F2A1C9E-7B0D-4E61-9C5A-2D8E4F1B3A70",
              "kind" : "text",
              "name" : "Rubrik",
              "opacity" : 1,
              "text" : {
                "alignment" : "center",
                "boxWidth" : 300,
                "color" : {
                  "b" : 0.2,
                  "g" : 0.1,
                  "r" : 0.9
                },
                "fontName" : "HelveticaNeue-Bold",
                "lineHeight" : 1.2,
                "onPathID" : "77777777-8888-4999-8AAA-BBBBBBBBBBBB",
                "origin" : [
                  10,
                  20
                ],
                "size" : 48,
                "string" : "Hej på dig",
                "styles" : [
                  {
                    "color" : {
                      "b" : 1,
                      "g" : 0,
                      "r" : 0
                    },
                    "length" : 3,
                    "location" : 0,
                    "size" : 60
                  }
                ],
                "tracking" : 25,
                "transform" : {
                  "a" : 1,
                  "b" : 0.2,
                  "c" : -0.2,
                  "d" : 1,
                  "tx" : 0,
                  "ty" : 0
                },
                "warp" : {
                  "bend" : -0.3,
                  "horizontal" : 0,
                  "style" : "flag",
                  "vertical" : 0.1
                }
              },
              "visible" : true
            }
          ],
          "expanded" : false,
          "formerText" : "Old words",
          "id" : "88888888-9999-4AAA-8BBB-CCCCCCCCCCCC",
          "kind" : "group",
          "name" : "Group 1",
          "opacity" : 1,
          "visible" : true
        }
      ],
      "link" : {
        "bookmark" : "AQID",
        "path" : "\/Users\/anders\/Pictures\/photo.png",
        "requester" : "Flimmer"
      },
      "paths" : [
        {
          "id" : "99999999-AAAA-4BBB-8CCC-DDDDDDDDDDDD",
          "name" : "Path 1",
          "subpaths" : [

          ]
        }
      ],
      "width" : 800
    }
    """;

    [Fact]
    public void AManifestWrittenOnTheMacReads()
    {
        var m = GlimmerPackage.DecodeManifest(Encoding.UTF8.GetBytes(MacManifest));
        Assert.True(m.Width == 800 && m.Height == 600 && m.ColorSpace == WorkingColorSpace.DisplayP3 && m.BlendSpace == BlendSpace.Gamma);
        Assert.Equal(Guid.Parse("6F2A1C9E-7B0D-4E61-9C5A-2D8E4F1B3A70"), m.ActiveLayerID);
        Assert.True(m.Aids is { RulersVisible: true, Snap: false } && m.Aids.Grid.Subdivisions == 2 && m.Aids.Guides[0].Position == 120.5);
        Assert.True(m.ImportReport is { LayersInFile: 5, MatchesTheFile: true } && m.ImportReport.Notes[0].Kind == PSDImportReport.Kind.FillLeftOut);
        Assert.Equal("/Users/anders/Pictures/photo.png", m.Link?.Path);
        Assert.Equal([1, 2, 3], m.Link?.Bookmark);
        Assert.Empty(m.Paths!.Value[0].Subpaths);

        var raster = m.Layers[0];
        Assert.True(raster.Kind == LayerRecordKind.Raster && raster.Name == "Bakgrund åäö" && raster.Mask is { DefaultValue: 0, Density: 0.7 });
        Assert.True(raster.Raw is { ByteCount: 3, PixelsChanged: null } && raster.Raw.Settings.Temperature == 5600 && raster.Raw.Settings.Tint is null);
        Assert.Null(raster.Clipped);

        var curves = m.Layers[1];
        Assert.True(curves.Clipped == true && curves.BlendMode == BlendMode.SoftLight && curves.Opacity == 0.8);
        var a = curves.Adjustment!;
        Assert.True(a.UsesNaturalCurve && a.Number("exposure") == 1.5 && a.Color("color") == new SRGBColor(0.92, 0.6, 0.2));
        Assert.True(a.Curve(CurveChannel.Red).SequenceEqual([new CurvePoint(0, 0.1), new CurvePoint(1, 0.9)]));
        Assert.Equal(3, a.Curve(CurveChannel.Rgb).Length);
        Assert.Equal(CurveMath.Identity, a.Curve(CurveChannel.Green));
        Assert.Equal(0.05, a.LevelsChannelFor(CurveChannel.Blue).InBlack);
        Assert.True(curves.Effects?.Stroke is { IsEnabled: false, Position: StrokePosition.Center } && curves.Effects.OuterGlow is null);

        var group = m.Layers[2];
        Assert.True(group.Kind == LayerRecordKind.Group && group.Expanded == false && group.FormerText == "Old words");
        var children = group.Children!.Value;
        var fill = Assert.IsType<Fill.Gradient>(children[0].Fill).Value;
        Assert.True(fill.Style == GradientStyle.Radial && fill.Reverse && fill.End == new Vec2(10, 20) && fill.Stops[1].Opacity == 0.5);
        Assert.Equal(new Vec2(5, 0), children[0].VectorMask!.Subpaths[0].Anchors[0].HandleOut);
        var smart = children[1].Smart!;
        Assert.True(!smart.Transform.IsAffine && smart.Transform.G == 0.001 && smart.Filters!.Value[0].Settings.Value("radius") == 6);
        var text = children[2].Text!;
        Assert.True(text.Alignment == TextAlignment.Center && text.BoxWidth == 300 && text.Tracking == 25 && text.String == "Hej på dig");
        Assert.True(text.Warp == new TextWarp(TextWarpStyle.Flag, -0.3, 0, 0.1) && text.Transform!.B == 0.2 && text.OnPath is null);
        Assert.Equal(60, text.Styles!.Value[0].Size);
    }

    [Fact]
    public void WhatIsReadIsWrittenBackInTheSameShapes()
    {
        var m = GlimmerPackage.DecodeManifest(Encoding.UTF8.GetBytes(MacManifest));
        var written = JsonSerializer.Serialize(m, SwiftJson.Pretty);
        // The same document, value for value: Swift's own decoder would read this back to what it wrote. (A flat-array
        // dictionary comes in Swift's hash order, which differs from run to run; it is compared as a dictionary.)
        var mac = PairsAsObjects(JsonNode.Parse(MacManifest)!);
        var ours = PairsAsObjects(JsonNode.Parse(written)!);
        Assert.True(JsonNode.DeepEquals(mac, ours), written);
        // And the keys come in the Mac's order.
        Assert.Equal(KeysInOrder(mac), KeysInOrder(ours));
    }

    /// <summary>The "curves" and "levels" flat arrays as objects with their keys in order.</summary>
    private static JsonNode PairsAsObjects(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    var value = o[key]!;
                    if (key is "curves" or "levels" && value is JsonArray pairs)
                    {
                        var map = new JsonObject();
                        foreach (int i in Enumerable.Range(0, pairs.Count / 2).OrderBy(i => pairs[2 * i]!.GetValue<string>(), StringComparer.Ordinal))
                            map[pairs[2 * i]!.GetValue<string>()] = pairs[2 * i + 1]!.DeepClone();
                        o[key] = map;
                    }
                    else
                    {
                        PairsAsObjects(value);
                    }
                }
                break;
            case JsonArray a:
                foreach (var item in a) if (item is not null) PairsAsObjects(item);
                break;
        }
        return node;
    }

    private static List<string> KeysInOrder(JsonNode node)
    {
        var keys = new List<string>();
        void Walk(JsonNode? n, string path)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                    {
                        keys.Add(path + "/" + k);
                        Walk(v, path + "/" + k);
                    }
                    break;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++) Walk(a[i], path + "[" + i + "]");
                    break;
            }
        }
        Walk(node, "");
        return keys;
    }

    [Fact]
    public void EnumsWithValuesAreOneKeyWithAnUnderscoreZero()
    {
        Assert.Equal("""{"solid":{"_0":{"b":0.25,"g":0.5,"r":1}}}""", SwiftJson.Serialize<Fill>(new Fill.Solid(new SRGBColor(1, 0.5, 0.25))));
        var pattern = SwiftJson.Deserialize<Fill>("""{"pattern":{"_0":{"background":{"b":1,"g":1,"r":1},"foreground":{"b":0,"g":0,"r":0},"kind":"dots","scale":12}}}""");
        Assert.Equal(PatternKind.Dots, Assert.IsType<Fill.Pattern>(pattern).Value.Kind);
        Assert.Throws<JsonException>(() => SwiftJson.Deserialize<Fill>("""{"solid":{"_0":{"r":1,"g":0,"b":0}},"pattern":{}}"""));
        Assert.Throws<JsonException>(() => SwiftJson.Deserialize<Fill>("""{"hatch":{"_0":{}}}"""));
    }

    [Fact]
    public void ADictionaryWithAnEnumKeyIsAFlatArray()
    {
        var a = new Adjustment(AdjustmentKind.Levels) { Levels = ImmutableDictionaryOf(CurveChannel.Green, new LevelsChannel(0.1)) };
        var json = JsonNode.Parse(SwiftJson.Serialize(a))!;
        Assert.Equal(JsonValueKind.Array, json["levels"]!.GetValueKind());
        Assert.Equal("green", json["levels"]![0]!.GetValue<string>());
        Assert.Throws<JsonException>(() => SwiftJson.Deserialize<Adjustment>("""{"kind":"levels","levels":["green"]}"""));
        Assert.Throws<JsonException>(() => SwiftJson.Deserialize<Adjustment>("""{"kind":"levels","levels":["teal",{"inBlack":0,"inWhite":1,"gamma":1,"outBlack":0,"outWhite":1}]}"""));
    }

    private static System.Collections.Immutable.ImmutableDictionary<CurveChannel, LevelsChannel> ImmutableDictionaryOf(CurveChannel c, LevelsChannel l) =>
        System.Collections.Immutable.ImmutableDictionary<CurveChannel, LevelsChannel>.Empty.Add(c, l);

    [Fact]
    public void SynthesizedTypesNeedEveryKeyAsOnTheMac()
    {
        // Property defaults don't make Swift's synthesized decoding lenient.
        Assert.Throws<JsonException>(() => SwiftJson.Deserialize<LevelsChannel>("""{"inBlack":0,"inWhite":1,"gamma":1,"outBlack":0}"""));
        Assert.Throws<JsonException>(() => SwiftJson.Deserialize<ShadowEffect>("""{"isEnabled":true}"""));
        Assert.Throws<JsonException>(() => SwiftJson.Deserialize<GridSettings>("""{"isVisible":true,"spacing":10}"""));
        Assert.Throws<JsonException>(() => SwiftJson.Deserialize<RawSettings>("""{"exposure":0,"shadows":1}"""));
        // An unknown raw value is an error too.
        Assert.Throws<JsonException>(() => SwiftJson.Deserialize<BlendMode>("\"normal-ish\""));
        // A nil optional is left out; a null one reads as nil.
        Assert.DoesNotContain("tint", SwiftJson.Serialize(new RawSettings()));
        Assert.Null(SwiftJson.Deserialize<RawSettings>("""{"exposure":0,"shadows":1,"highlightRecovery":false,"tint":null}""").Tint);
    }

    [Fact]
    public void TheLenientDecodersAreAsLenientAsTheMacs()
    {
        // TextWarp: every field falls back, a wrong type too.
        Assert.Equal(new TextWarp(TextWarpStyle.None, 0, 0, 0), SwiftJson.Deserialize<TextWarp>("{}"));
        Assert.Equal(new TextWarp(TextWarpStyle.None, 0.5, 0, 0), SwiftJson.Deserialize<TextWarp>("""{"style":"twist","bend":0.5,"horizontal":"x"}"""));
        // AffineTransform: g and h may be missing, and are left out when affine.
        Assert.Equal(new AffineTransform(2, 0, 0, 2, 5, 6), SwiftJson.Deserialize<AffineTransform>("""{"a":2,"b":0,"c":0,"d":2,"tx":5,"ty":6}"""));
        Assert.Equal("""{"a":1,"b":0,"c":0,"d":1,"tx":0,"ty":0}""", SwiftJson.Serialize(AffineTransform.Identity));
        // Adjustment: only the kind is needed.
        var a = SwiftJson.Deserialize<Adjustment>("""{"kind":"photo_filter"}""");
        Assert.Equal(0.25, a.Number("density"));
        Assert.True(GradientStop.BlackToWhite.SequenceEqual(a.Gradient));
    }

    [Fact]
    public void PointsAreTwoNumberArraysAndIdsAreUpperCase()
    {
        var path = new VectorPath("p", [new Subpath([new PathAnchor(new Vec2(1.5, -2))])], Guid.Parse("0e0f1a2b-3c4d-4e5f-8061-728394a5b6c7"));
        var json = SwiftJson.Serialize(path);
        Assert.Contains("\"id\":\"0E0F1A2B-3C4D-4E5F-8061-728394A5B6C7\"", json);
        Assert.Contains("\"point\":[1.5,-2]", json);
        Assert.Equal(path, SwiftJson.Deserialize<VectorPath>(json.Replace("0E0F1A2B-3C4D-4E5F-8061-728394A5B6C7", "0e0f1a2b-3c4d-4e5f-8061-728394a5b6c7")));
        Assert.Throws<JsonException>(() => SwiftJson.Deserialize<Vec2>("[1, 2, 3]"));
        Assert.Throws<JsonException>(() => SwiftJson.Deserialize<VectorPath>("""{"id":"not-a-uuid","name":"p","subpaths":[]}"""));
    }

    [Fact]
    public void KeysAreSortedAsSwiftSortsThem()
    {
        string[] keys = ["b", "a10", "a9", "B2", "a", "A1"];
        Assert.Equal(["a", "A1", "a9", "a10", "b", "B2"], keys.Order(SwiftJson.KeyOrder));
        var a = new Adjustment(AdjustmentKind.ChannelMixer) { Numbers = System.Collections.Immutable.ImmutableDictionary.CreateRange([new KeyValuePair<string, double>("rr", 1), new("gb", 2), new("bc", 3)]) };
        Assert.Contains("\"numbers\":{\"bc\":3,\"gb\":2,\"rr\":1}", SwiftJson.Serialize(a));
    }
}
