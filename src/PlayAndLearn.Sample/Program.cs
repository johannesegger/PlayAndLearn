﻿using System;
using PlayAndLearn.Models;
using PlayAndLearn.Utils;

namespace PlayAndLearn.Sample
{
    class Program
    {
        static void Main(string[] args)
        {
            Game.ShowSceneAndAddTurtle();
            // Game.SetSlowMotion();

            // Program1();
            // Program2();
            // Program3();
            // Program4();
            // Program5();
            // Program6();
            // Program7();
            Program8();
        }

        private static void Program1()
        {
            Turtle.GoTo(0, 0);
            Turtle.SetPenWeight(1.5);
            Turtle.SetPenColor(RGBColor.Cyan);
            Turtle.TurnOnPen();
            var n = 5;
            while (n < 400)
            {
                Turtle.Go(n);
                Turtle.RotateCounterClockwise(89.5);

                Turtle.ShiftPenColor(10.0 / 360);
                n++;

                Game.Sleep(10);
            }
        }

        private static void Program2()
        {
            Turtle.GoTo(0, 0);
            for (int i = 0; i < 36; i++)
            {
                Turtle.RotateClockwise(10);
                Turtle.Go(10);
            }
        }

        private static void Program3()
        {
            Turtle.GoTo(0, 0);
            Turtle.Say("Let's do it", 2);
            for (var i = 0; i < 10; i++)
            {
                Turtle.Go(10);
            }
            Turtle.Say("Nice one");
            for (var i = 0; i < 10; i++)
            {
                Turtle.Go(-10);
            }
            Turtle.ShutUp();
            for (var i = 0; i < 10; i++)
            {
                Turtle.Go(10);
            }
            Turtle.Say("Done");
        }

        private static void Program4()
        {
            Turtle.GoTo(0, 0);
            Turtle.SetPenWeight(1.5);
            Turtle.SetPenColor(RGBColor.Cyan);
            Turtle.TurnOnPen();
            for (var i = 0; i < 10; i++)
            {
                Turtle.Go(10);
            }
            Game.ClearScene();
            for (var i = 0; i < 10; i++)
            {
                Turtle.Go(-10);
            }
            Game.ClearScene();
            for (var i = 0; i < 10; i++)
            {
                Turtle.Go(10);
            }
            Game.ClearScene();
        }

        private static void Program5()
        {
            Turtle.OnKeyDown(KeyboardKey.Up, player => player.MoveUp(10));
            Turtle.OnKeyDown(KeyboardKey.Down, player => player.MoveDown(10));
            Turtle.OnKeyDown(KeyboardKey.Left, player => player.MoveLeft(10));
            Turtle.OnKeyDown(KeyboardKey.Right, player => player.MoveRight(10));
        }

        private static void Program6()
        {
            var randomNumberGenerator = new Random();
            Turtle.OnMouseEnter(player =>
                player.GoTo(
                    randomNumberGenerator.Next((int)Game.Bounds.Left, (int)Game.Bounds.Right),
                    randomNumberGenerator.Next((int)Game.Bounds.Bottom, (int)Game.Bounds.Top)));
        }

        private static void Program7()
        {
            Turtle.Say("Try and hit me, sucker!", 2);
            Turtle.OnClick(player => player.Say("Ouch, that hurts!", 2));
        }

        private static void Program8()
        {
            for (int i = 0; i < 500; i++)
            {
                Turtle.Say(new string('A', i));
                Game.Sleep(20);
            }
        }
    }
}