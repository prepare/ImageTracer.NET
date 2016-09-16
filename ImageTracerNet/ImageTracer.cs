﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ImageTracerNet.Extensions;
using ImageTracerNet.Palettes;
using System.Windows.Media.Imaging;
using ImageTracerNet.OptionTypes;
using ImageTracerNet.Vectorization;
using ImageTracerNet.Vectorization.Points;
using TriListIntArray = System.Collections.Generic.List<System.Collections.Generic.List<System.Collections.Generic.List<int[]>>>; // ArrayList<ArrayList<ArrayList<Integer[]>>>
using TriListDoubleArray = System.Collections.Generic.List<System.Collections.Generic.List<System.Collections.Generic.List<double[]>>>; // ArrayList<ArrayList<ArrayList<Double[]>>>

namespace ImageTracerNet
{
    public static class ImageTracer
    {
        public static readonly string VersionNumber = typeof(ImageTracer).Assembly.GetName().Version.ToString();

        private static readonly Random Rng = new Random();

        ////////////////////////////////////////////////////////////
        //
        //  User friendly functions
        //
        ////////////////////////////////////////////////////////////

        // Loading an image from a file, tracing when loaded, then returning the SVG String
        public static string ImageToSvg(string filename, Options options, byte[][] palette) 
        {
            return ImageToSvg(new Bitmap(filename), options, palette);
        }

        public static string ImageToSvg(Bitmap image, Options options, byte[][] palette) 
        {
            return ImageDataToSvg(image, LoadImageData(image), options, palette);
        }

        // Loading an image from a file, tracing when loaded, then returning IndexedImage with tracedata in layers
        internal static IndexedImage ImageToTraceData(string filename, Options options, byte[][] palette) 
        {
            return ImageToTraceData(new Bitmap(filename), options, palette);
        }

        internal static IndexedImage ImageToTraceData(Bitmap image, Options options, byte[][] palette) 
        {
            return ImageDataToTraceData(image, LoadImageData(image), options, palette);
        }

        ////////////////////////////////////////////////////////////

        private static ImageData LoadImageData(Bitmap image)
        {
            var rbgImage = image.ChangeFormat(PixelFormat.Format32bppArgb);
            var data = rbgImage.ToRgbaByteArray();
            return new ImageData(image.Width, image.Height, data);
        }

        // Tracing ImageData, then returning the SVG String
        private static string ImageDataToSvg(Bitmap image, ImageData imgd, Options options, byte[][] palette)
        {
            return GetSvgString(ImageDataToTraceData(image, imgd, options, palette), options);
        }

        // Tracing ImageData, then returning IndexedImage with tracedata in layers
        private static IndexedImage ImageDataToTraceData(Bitmap image, ImageData imgd, Options options, byte[][] palette)
        {
            //var paletteRowsColumns = (int)Math.Sqrt(options.ColorQuantization.NumberOfColors);
            // Use custom palette if pal is defined or sample or generate custom length palette
            //var colorPalette = palette != null 
            //    ? ColorExtensions.FromRgbaByteArray(palette.SelectMany(c => c).ToArray()) 
            //    : SmartPalette.Generate(image, paletteRowsColumns, paletteRowsColumns);

            //colorPalette = colorPalette ?? (options.ColorQuantization.ColorSampling.IsNotZero()
            //        ? PaletteGenerator.SamplePalette(options.ColorQuantization.NumberOfColors, imgd)
            //        : PaletteGenerator.GeneratePalette(options.ColorQuantization.NumberOfColors));

            var colorPalette = BitmapPalettes.Halftone256.Colors.Select(c => Color.FromArgb(c.A, c.R, c.G, c.B)).ToArray();

            // Selective Gaussian blur preprocessing
            if (options.Blur.BlurRadius > 0)
            {
                // TODO: This seems to not work currently.
                imgd = Blur(imgd, options.Blur.BlurRadius, options.Blur.BlurDelta);
            }

            // 1. Color quantization
            var ii = IndexedImage.Create(imgd, colorPalette, options.ColorQuantization);
            // 2. Layer separation and edge detection
            var rawLayers = Layering(ii);
            // 3. Batch pathscan
            var bps = rawLayers.Select(layer => Pathing.Scan(layer, options.Tracing.PathOmit)).ToList();
            // 4. Batch interpollation
            var bis = bps.Select(Interpolation.Convert).ToList();
            // 5. Batch tracing
            ii.Layers = bis.Select(l => l.Select(p => TracePath(p, options.Tracing).ToList()).ToList()).ToList();
            
            return ii;
        }

        ////////////////////////////////////////////////////////////
        //
        //  Vectorizing functions
        //
        ////////////////////////////////////////////////////////////

        // 2. Layer separation and edge detection

        // Edge node types ( ▓:light or 1; ░:dark or 0 )

        // 12  ░░  ▓░  ░▓  ▓▓  ░░  ▓░  ░▓  ▓▓  ░░  ▓░  ░▓  ▓▓  ░░  ▓░  ░▓  ▓▓

        // 48  ░░  ░░  ░░  ░░  ░▓  ░▓  ░▓  ░▓  ▓░  ▓░  ▓░  ▓░  ▓▓  ▓▓  ▓▓  ▓▓
        //     0   1   2   3   4   5   6   7   8   9   10  11  12  13  14  15
        private static int[][][] Layering(IndexedImage ii)
        {
            // Creating layers for each indexed color in arr
            var layers = new int[ii.Palette.Length][][].InitInner(ii.ArrayHeight, ii.ArrayWidth);

            // Looping through all pixels and calculating edge node type
            for (var j = 1; j < ii.ArrayHeight - 1; j++)
            {
                for (var i = 1; i < ii.ArrayWidth - 1; i++)
                {
                    // This pixel's indexed color
                    var pg = ii.GetPixelGroup(j, i);

                    // Are neighbor pixel colors the same?
                    // this pixel's type and looking back on previous pixels
                    // X
                    // 1, 3, 5, 7, 9, 11, 13, 15
                    layers[pg.Mid][j + 1][i + 1] = 1 + Convert.ToInt32(pg.MidRight == pg.Mid) * 2 + Convert.ToInt32(pg.BottomRight == pg.Mid) * 4 + Convert.ToInt32(pg.BottomMid == pg.Mid) * 8;
                    if (pg.MidLeft != pg.Mid)
                    {
                        // A
                        // 2, 6, 10, 14
                        layers[pg.Mid][j + 1][i] = 2 + Convert.ToInt32(pg.BottomMid == pg.Mid) * 4 + Convert.ToInt32(pg.BottomLeft == pg.Mid) * 8;
                    }
                    if (pg.TopMid != pg.Mid)
                    {
                        // B
                        // 8, 10, 12, 14
                        layers[pg.Mid][j][i + 1] = 8 + Convert.ToInt32(pg.TopRight == pg.Mid) * 2 + Convert.ToInt32(pg.MidRight == pg.Mid) * 4;
                    }
                    if (pg.TopLeft != pg.Mid)
                    {
                        // C
                        // 4, 6, 12, 14
                        layers[pg.Mid][j][i] = 4 + Convert.ToInt32(pg.TopMid == pg.Mid) * 2 + Convert.ToInt32(pg.MidLeft == pg.Mid) * 8;
                    }
                }
            }

            return layers;
        }

        // 5. tracepath() : recursively trying to fit straight and quadratic spline segments on the 8 direction internode path

        // 5.1. Find sequences of points with only 2 segment types
        // 5.2. Fit a straight line on the sequence
        // 5.3. If the straight line fails (an error>ltreshold), find the point with the biggest error
        // 5.4. Fit a quadratic spline through errorpoint (project this to get controlpoint), then measure errors on every point in the sequence
        // 5.5. If the spline fails (an error>qtreshold), find the point with the biggest error, set splitpoint = (fitting point + errorpoint)/2
        // 5.6. Split sequence and recursively apply 5.2. - 5.7. to startpoint-splitpoint and splitpoint-endpoint sequences
        // 5.7. TODO? If splitpoint-endpoint is a spline, try to add new points from the next sequence

        // This returns an SVG Path segment as a double[7] where
        // segment[0] ==1.0 linear  ==2.0 quadratic interpolation
        // segment[1] , segment[2] : x1 , y1
        // segment[3] , segment[4] : x2 , y2 ; middle point of Q curve, endpoint of L line
        // segment[5] , segment[6] : x3 , y3 for Q curve, should be 0.0 , 0.0 for L line
        //
        // path type is discarded, no check for path.size < 3 , which should not happen

        private static IEnumerable<double[]> TracePath(List<InterpolationPoint> path, Tracing tracingOptions)
        {
            var sequences = Sequencing.Create(path.Select(p => p.Direction).ToList());
            // Fit the sequences into segments, and return them.
            return sequences.Select(s => Segmentation.Fit(path, tracingOptions, s.Item1, s.Item2)).SelectMany(s => s);
        }

        ////////////////////////////////////////////////////////////
        //
        //  SVG Drawing functions
        //
        ////////////////////////////////////////////////////////////

        // Getting SVG path element string from a traced path
        private static void SvgPathString(StringBuilder stringBuilder, string desc, IReadOnlyList<double[]> segments, string colorstr, Options options)
        {
            var scale = options.SvgRendering.Scale;
            var linearControlPointRadius = options.SvgRendering.LCpr;
            var quadraticControlPointRadius = options.SvgRendering.LCpr;
            var roundCoords = options.SvgRendering.RoundCoords;
            // Path
            stringBuilder.Append($"<path {desc}{colorstr}d=\"M {segments[0][1] * scale} {segments[0][2] * scale} ");
            foreach (var segment in segments)
            {
                string segmentAsString;
                if (roundCoords == -1)
                {
                    segmentAsString = segment[0].AreEqual(1.0)
                        ? $"L {segment[3]*scale} {segment[4]*scale} "
                        : $"Q {segment[3]*scale} {segment[4]*scale} {segment[5]*scale} {segment[6]*scale} ";
                }
                else
                {
                    segmentAsString = segment[0].AreEqual(1.0)
                        ? $"L {Math.Round(segment[3]*scale, roundCoords)} {Math.Round(segment[4]*scale, roundCoords)} "
                        : $"Q {Math.Round(segment[3]*scale, roundCoords)} {Math.Round(segment[4]*scale, roundCoords)} {Math.Round(segment[5]*scale, roundCoords)} {Math.Round(segment[6]*scale, roundCoords)} ";
                } // End of roundcoords check

                stringBuilder.Append(segmentAsString);
            }

            stringBuilder.Append("Z\" />");

            // Rendering control points
            foreach (var segment in segments)
            {
                if ((linearControlPointRadius > 0) && segment[0].AreEqual(1.0))
                {
                    stringBuilder.Append($"<circle cx=\"{segment[3] * scale}\" cy=\"{segment[4] * scale}\" r=\"{linearControlPointRadius}\" fill=\"white\" stroke-width=\"{linearControlPointRadius * 0.2}\" stroke=\"black\" />");
                }
                if ((quadraticControlPointRadius > 0) && segment[0].AreEqual(2.0))
                {
                    stringBuilder.Append($"<circle cx=\"{segment[3] * scale}\" cy=\"{segment[4] * scale}\" r=\"{quadraticControlPointRadius}\" fill=\"cyan\" stroke-width=\"{quadraticControlPointRadius * 0.2}\" stroke=\"black\" />");
                    stringBuilder.Append($"<circle cx=\"{segment[5] * scale}\" cy=\"{segment[6] * scale}\" r=\"{quadraticControlPointRadius}\" fill=\"white\" stroke-width=\"{quadraticControlPointRadius * 0.2}\" stroke=\"black\" />");
                    stringBuilder.Append($"<line x1=\"{segment[1] * scale}\" y1=\"{segment[2] * scale}\" x2=\"{segment[3] * scale}\" y2=\"{segment[4] * scale}\" stroke-width=\"{quadraticControlPointRadius * 0.2}\" stroke=\"cyan\" />");
                    stringBuilder.Append($"<line x1=\"{segment[3] * scale}\" y1=\"{segment[4] * scale}\" x2=\"{segment[5] * scale}\" y2=\"{segment[6] * scale}\" stroke-width=\"{quadraticControlPointRadius * 0.2}\" stroke=\"cyan\" />");
                }// End of quadratic control points
            }
        }

        // Converting tracedata to an SVG string, paths are drawn according to a Z-index
        // the optional lcpr and qcpr are linear and quadratic control point radiuses
        private static string GetSvgString(IndexedImage ii, Options options)
        {
            // SVG start
            var width = (int)(ii.ImageWidth * options.SvgRendering.Scale);
            var height = (int)(ii.ImageHeight * options.SvgRendering.Scale);

            var viewBoxOrViewPort = options.SvgRendering.Viewbox.IsNotZero() ? 
                $"viewBox=\"0 0 {width} {height}\"" : 
                $"width=\"{width}\" height=\"{height}\"";
            var svgStringBuilder = new StringBuilder($"<svg {viewBoxOrViewPort} version=\"1.1\" xmlns=\"http://www.w3.org/2000/svg\" ");
            if (options.SvgRendering.Desc.IsNotZero())
            {
                svgStringBuilder.Append($"desc=\"Created with ImageTracer.NET version {VersionNumber}\" ");
            }
            svgStringBuilder.Append(">");

            // creating Z-index
            var zIndex = new SortedDictionary<double, ZPosition>();
            // Layer loop
            for (var layerIndex = 0; layerIndex < ii.Layers.Count; layerIndex++)
            {
                // Path loop
                for (var pathIndex = 0; pathIndex < ii.Layers[layerIndex].Count; pathIndex++)
                {
                    // Label (Z-index key) is the startpoint of the path, linearized
                    var label = ii.Layers[layerIndex][pathIndex][0][2] * width + ii.Layers[layerIndex][pathIndex][0][1];
                    zIndex[label] = new ZPosition { Layer = layerIndex, Path = pathIndex };
                }
            }

            // Sorting Z-index is not required, TreeMap is sorted automatically

            // Drawing
            // Z-index loop
            foreach(var zPosition in zIndex)
            {
                var zValue = zPosition.Value;
                var description = String.Empty;
                if (options.SvgRendering.Desc.IsNotZero())
                {
                    description = $"desc=\"l {zValue.Layer} p {zValue.Path}\" ";
                }

                SvgPathString(svgStringBuilder, description, ii.Layers[zValue.Layer][zValue.Path], 
                    ToSvgColorString(ii.Palette[zValue.Layer]), options);
            }

            // SVG End
            svgStringBuilder.Append("</svg>");

            return svgStringBuilder.ToString();
        }

        private static string ToSvgColorString(byte[] c)
        {
            const int shift = 0; // MJY: Try removing all the + 128 on the values. Might fix issues.
            return "fill=\"rgb(" + (c[0] + shift) + "," + (c[1] + shift) + "," + (c[2] + shift) + ")\" stroke=\"rgb(" + (c[0] + shift) + "," + (c[1] + shift) + "," + (c[2] + shift) + ")\" stroke-width=\"1\" opacity=\"" + (c[3] + shift) / 255.0 + "\" ";
        }

        // Gaussian kernels for blur
        private static readonly double[][] Gks = 
        {
            new []{0.27901, 0.44198, 0.27901},
            new []{0.135336, 0.228569, 0.272192, 0.228569, 0.135336},
            new []{0.086776, 0.136394, 0.178908, 0.195843, 0.178908, 0.136394, 0.086776},
            new []{0.063327, 0.093095, 0.122589, 0.144599, 0.152781, 0.144599, 0.122589, 0.093095, 0.063327},
            new []{0.049692, 0.069304, 0.089767, 0.107988, 0.120651, 0.125194, 0.120651, 0.107988, 0.089767, 0.069304, 0.049692}
        };

        // Selective Gaussian blur for preprocessing
        private static ImageData Blur(ImageData imgd, int rad, double del)
        {
            int i, j, k;
            int idx;
            double racc, gacc, bacc, aacc, wacc;
            var imgd2 = new ImageData(imgd.Width, imgd.Height, new byte[imgd.Width * imgd.Height * 4]);

            // radius and delta limits, this kernel
            var radius = rad; if (radius < 1) { return imgd; }
            if (radius > 5) { radius = 5; }
            var delta = (int)Math.Abs(del); if (delta > 1024) { delta = 1024; }
            var thisgk = Gks[radius - 1];

            // loop through all pixels, horizontal blur
            for (j = 0; j < imgd.Height; j++)
            {
                for (i = 0; i < imgd.Width; i++)
                {
                    racc = 0; gacc = 0; bacc = 0; aacc = 0; wacc = 0;
                    // gauss kernel loop
                    for (k = -radius; k < radius + 1; k++)
                    {
                        // add weighted color values
                        if ((i + k > 0) && (i + k < imgd.Width))
                        {
                            idx = (j * imgd.Width + i + k) * 4;
                            racc += imgd.Data[idx] * thisgk[k + radius];
                            gacc += imgd.Data[idx + 1] * thisgk[k + radius];
                            bacc += imgd.Data[idx + 2] * thisgk[k + radius];
                            aacc += imgd.Data[idx + 3] * thisgk[k + radius];
                            wacc += thisgk[k + radius];
                        }
                    }
                    // The new pixel
                    idx = (j * imgd.Width + i) * 4;
                    imgd2.Data[idx] = (byte)Math.Floor(racc / wacc);
                    imgd2.Data[idx + 1] = (byte)Math.Floor(gacc / wacc);
                    imgd2.Data[idx + 2] = (byte)Math.Floor(bacc / wacc);
                    imgd2.Data[idx + 3] = (byte)Math.Floor(aacc / wacc);

                }// End of width loop
            }// End of horizontal blur

            // copying the half blurred imgd2
            var himgd = imgd2.Data.Clone() as byte[];

            // loop through all pixels, vertical blur
            for (j = 0; j < imgd.Height; j++)
            {
                for (i = 0; i < imgd.Width; i++)
                {
                    racc = 0; gacc = 0; bacc = 0; aacc = 0; wacc = 0;
                    // gauss kernel loop
                    for (k = -radius; k < radius + 1; k++)
                    {
                        // add weighted color values
                        if ((j + k > 0) && (j + k < imgd.Height))
                        {
                            idx = ((j + k) * imgd.Width + i) * 4;
                            racc += himgd[idx] * thisgk[k + radius];
                            gacc += himgd[idx + 1] * thisgk[k + radius];
                            bacc += himgd[idx + 2] * thisgk[k + radius];
                            aacc += himgd[idx + 3] * thisgk[k + radius];
                            wacc += thisgk[k + radius];
                        }
                    }
                    // The new pixel
                    idx = (j * imgd.Width + i) * 4;
                    imgd2.Data[idx] = (byte)Math.Floor(racc / wacc);
                    imgd2.Data[idx + 1] = (byte)Math.Floor(gacc / wacc);
                    imgd2.Data[idx + 2] = (byte)Math.Floor(bacc / wacc);
                    imgd2.Data[idx + 3] = (byte)Math.Floor(aacc / wacc);
                }// End of width loop
            }// End of vertical blur

            // Selective blur: loop through all pixels
            for (j = 0; j < imgd.Height; j++)
            {
                for (i = 0; i < imgd.Width; i++)
                {
                    idx = (j * imgd.Width + i) * 4;
                    // d is the difference between the blurred and the original pixel
                    var d = Math.Abs(imgd2.Data[idx] - imgd.Data[idx]) + Math.Abs(imgd2.Data[idx + 1] - imgd.Data[idx + 1]) +
                            Math.Abs(imgd2.Data[idx + 2] - imgd.Data[idx + 2]) + Math.Abs(imgd2.Data[idx + 3] - imgd.Data[idx + 3]);
                    // selective blur: if d>delta, put the original pixel back
                    if (d > delta)
                    {
                        imgd2.Data[idx] = imgd.Data[idx];
                        imgd2.Data[idx + 1] = imgd.Data[idx + 1];
                        imgd2.Data[idx + 2] = imgd.Data[idx + 2];
                        imgd2.Data[idx + 3] = imgd.Data[idx + 3];
                    }
                }
            }// End of Selective blur
            return imgd2;
        }
    }
}
