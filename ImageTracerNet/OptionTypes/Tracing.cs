﻿using System;

namespace ImageTracerNet.OptionTypes
{
    [Serializable]
    public class Tracing
    {
        public double LTres { get; set; } = 1f;
        public double QTres { get; set; } = 1f;
        public double PathOmit { get; set; } = 8f;
    }
}
