using System;

namespace PlayAndLearn.Models
{
    [Equals]
    public sealed class Size
    {
        public Size(double width, double height)
        {
            Width = width;
            Height = height;
        }

        public double Width { get; }
        public double Height { get; }
    }
}