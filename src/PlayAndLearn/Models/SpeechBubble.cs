using System;

namespace PlayAndLearn.Utils
{
    public class SpeechBubble
    {
        public static readonly SpeechBubble Empty = new SpeechBubble("");
        
        public SpeechBubble(string text)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
        }

        public string Text { get; }
    }
}