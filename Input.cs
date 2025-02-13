﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vortice.XInput;
using static PvZA11y.Program;
using System.Collections.Immutable;
using System.Xml.Xsl;
using System.Text.Json.Serialization;

namespace PvZA11y
{

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum InputIntent
    {
        None,       //Do nothing
        Up,
        Down,
        Left,
        Right,
        Confirm,    //A
        Deny,       //B
        Start,      //Start
        Option,     //Select
        CycleLeft,  //Lb
        CycleRight, //Rb
        Info1,      //X
        Info2,      //Y
        Info3,      //L3
        Info4,      //R3
    }

    public class Input
    {
        bool[] heldKeys = new bool[0xff + 1]; //Array of all currently-held keys
        State prevState;

        Dictionary<uint, InputIntent> keyBinds = new Dictionary<uint, InputIntent>();
        Dictionary<GamepadButtons, InputIntent> controllerBinds = new Dictionary<GamepadButtons, InputIntent>();

        bool keybindsChanged = false;
        bool running = true;

        short xThreshold = 20000;
        short yThreshold = 20000;

        ConcurrentQueue<InputIntent> inputQueue = new ConcurrentQueue<InputIntent>();


        //TODO: Make these options user-customisable in accessibility menu
        int initialKeyRepeatDelay = 400;    //Delay before keys start being repeated
        int keyRepeatDelay = 100;   //Delay between key repetitions, after the initial timer has been exceeded

        //TODO: Make this a bit nicer
        Dictionary<InputIntent, long> keyRepeatTimers = new Dictionary<InputIntent, long>() { { InputIntent.Up, 0 }, { InputIntent.Down, 0 }, { InputIntent.Left, 0 }, { InputIntent.Right, 0 }, { InputIntent.CycleLeft, 0 }, { InputIntent.CycleRight, 0 } };

        public Input()
        {
            bool controllerBindsOk = true;
            if (Config.current.controllerBinds != null)
            {
                foreach (InputIntent intent in Enum.GetValues(typeof(InputIntent)))
                {
                    if (intent is InputIntent.None)
                        continue;
                    if (!Config.current.controllerBinds.ContainsValue(intent))
                    {
                        controllerBindsOk = false;
                        break;
                    }
                }

            }
            else
                controllerBindsOk = false;

            if (controllerBindsOk)
                controllerBinds = Config.current.controllerBinds;
            else
            {
                controllerBinds = new Dictionary<GamepadButtons, InputIntent>();

                controllerBinds.Add(GamepadButtons.DPadUp, InputIntent.Up);
                controllerBinds.Add(GamepadButtons.DPadDown, InputIntent.Down);
                controllerBinds.Add(GamepadButtons.DPadLeft, InputIntent.Left);
                controllerBinds.Add(GamepadButtons.DPadRight, InputIntent.Right);

                controllerBinds.Add(GamepadButtons.A, InputIntent.Confirm);
                controllerBinds.Add(GamepadButtons.B, InputIntent.Deny);

                controllerBinds.Add(GamepadButtons.Start, InputIntent.Start);
                controllerBinds.Add(GamepadButtons.Back, InputIntent.Option);

                controllerBinds.Add(GamepadButtons.LeftShoulder, InputIntent.CycleLeft);
                controllerBinds.Add(GamepadButtons.RightShoulder, InputIntent.CycleRight);

                controllerBinds.Add(GamepadButtons.X, InputIntent.Info1);
                controllerBinds.Add(GamepadButtons.Y, InputIntent.Info2);
                controllerBinds.Add(GamepadButtons.LeftThumb, InputIntent.Info3);
                controllerBinds.Add(GamepadButtons.RightThumb, InputIntent.Info4);

                Config.current.controllerBinds = controllerBinds;
                Config.SaveConfig();
            }

            bool keyboardBindsOk = true;

            if (Config.current.keyBinds != null)
            {
                foreach (InputIntent intent in Enum.GetValues(typeof(InputIntent)))
                {
                    if (intent is InputIntent.None)
                        continue;
                    if (!Config.current.keyBinds.ContainsValue(intent))
                    {
                        keyboardBindsOk = false;
                        break;
                    }
                }

            }
            else
                keyboardBindsOk = false;

            if (keyboardBindsOk)
                keyBinds = Config.current.keyBinds;
            else
            {
                keyBinds = new Dictionary<uint, InputIntent>();

                keyBinds.Add(VIRTUALKEY.VK_UP, InputIntent.Up);
                keyBinds.Add(VIRTUALKEY.VK_DOWN, InputIntent.Down);
                keyBinds.Add(VIRTUALKEY.VK_LEFT, InputIntent.Left);
                keyBinds.Add(VIRTUALKEY.VK_RIGHT, InputIntent.Right);

                keyBinds.Add(VIRTUALKEY.VK_RETURN, InputIntent.Confirm);
                keyBinds.Add(VIRTUALKEY.VK_BACK, InputIntent.Deny);

                keyBinds.Add(VIRTUALKEY.VK_ESCAPE, InputIntent.Start);
                keyBinds.Add(VIRTUALKEY.VK_TAB, InputIntent.Option);

                keyBinds.Add(VIRTUALKEY.VK_OEM_MINUS, InputIntent.CycleLeft);
                keyBinds.Add(VIRTUALKEY.VK_OEM_PLUS, InputIntent.CycleRight);

                keyBinds.Add(VIRTUALKEY.VK_F1, InputIntent.Info1);
                keyBinds.Add(VIRTUALKEY.VK_F2, InputIntent.Info2);
                keyBinds.Add(VIRTUALKEY.VK_F3, InputIntent.Info3);
                keyBinds.Add(VIRTUALKEY.VK_F4, InputIntent.Info4);

                Config.current.keyBinds = keyBinds;
                Config.SaveConfig();
            }

            Task.Run(InputScanThread);
        }

        public InputIntent GetCurrentIntent()
        {
            if(inputQueue.TryDequeue(out var intent))
                return intent;

            return InputIntent.None;
        }

        private void InputScanThread()
        {
            while (running)
            {
                var iControllerBinds = controllerBinds.ToImmutableDictionary();
                var iKeyBinds = keyBinds.ToImmutableDictionary();
                keybindsChanged = false;
                inputQueue.Clear(); //Clear intents if keybinds have changed

                while (!keybindsChanged)
                {
                    var intents = GetCurrentIntents(iKeyBinds,iControllerBinds);
                    foreach (var intent in intents)
                    {
                        //Try to avoid queuing up too many unprocessed inputs
                        //Without this, it was possible to queue thousands of inputs by holding a few directional inputs at the same time
                        if (inputQueue.Count < 8)
                            inputQueue.Enqueue(intent);
                    }

                    Thread.Sleep(5);    //Just a little delay, to avoid scanning billions of times per second
                }
            }

        }

        public void ClearIntents()
        {
            inputQueue.Clear();
        }

        public GamepadButtons GetButton()
        {
            State state;

            //Wait until no buttons are being pressed, then return first button pressed after that
            bool buttonPressed = true;
            while (buttonPressed)
            {
                if (XInput.GetState(0, out state))
                {
                    buttonPressed = false;
                    foreach (GamepadButtons button in Enum.GetValues(typeof(GamepadButtons)))
                    {
                        if (button is GamepadButtons.None)
                            continue;
                        if (state.Gamepad.Buttons.HasFlag(button))
                            buttonPressed = true;
                    }
                }
                else
                    return GamepadButtons.None;
            }

            while (true)
            {
                if (XInput.GetState(0, out state))
                {
                    foreach (GamepadButtons button in Enum.GetValues(typeof(GamepadButtons)))
                    {
                        if (button is GamepadButtons.None)
                            continue;
                        if (state.Gamepad.Buttons.HasFlag(button))
                            return button;
                    }

                    if (NativeKeyboard.IsKeyDown(VIRTUALKEY.VK_ESCAPE))
                        return GamepadButtons.None;

                }
                else
                    return GamepadButtons.None;
            }
        }

        public void WaitForNoInput()
        {
            ClearIntents();
            bool buttonPressed = true;
            while (buttonPressed)
            {
                buttonPressed = false;
                for (uint i = 1; i < 0xff; i++)
                {
                    if (NativeKeyboard.IsKeyDown(i))
                        buttonPressed = true;
                }
            }
            ClearIntents();
        }

        public uint GetKey(bool blocking = true)
        {
            //Wait until no keys are being pressed, then return first key pressed after
            bool buttonPressed = true;
            while(buttonPressed)
            {
                buttonPressed = false;
                for(uint i = 1; i < 0xff; i++)
                {
                    if (NativeKeyboard.IsKeyDown(i))
                        buttonPressed = true;
                }
                if (!blocking && buttonPressed)
                    return 0;
            }

            while(true)
            {
                for (uint i = 1; i < 0xff; i++)
                {
                    if (NativeKeyboard.IsKeyDown(i))
                        return i;
                }
                if (!blocking)
                    return 0;
            }

        }

        public void GetKeyOrButton(ref uint pressedKey, ref GamepadButtons pressedButton)
        {
            pressedKey = 1;
            pressedButton = GamepadButtons.Start;

            string startWarning = "Please release all keyboard keys and controller buttons.";

            while (pressedKey != 0 || pressedButton != GamepadButtons.None)
            {
                pressedButton = GamepadButtons.None;
                pressedKey = 0;

                if (XInput.GetState(0, out State state))
                {
                    foreach (GamepadButtons button in Enum.GetValues(typeof(GamepadButtons)))
                    {
                        if (button is GamepadButtons.None)
                            continue;
                        if (state.Gamepad.Buttons.HasFlag(button))
                        {
                            pressedButton = button;
                            break;
                        }
                    }
                }
                
                for (uint i = 1; i < 0xff; i++)
                {
                    if (NativeKeyboard.IsKeyDown(i))
                    {
                        pressedKey = i;
                        break;
                    }

                }

            }


            while (pressedKey == 0 && pressedButton == GamepadButtons.None)
            {
                if (XInput.GetState(0, out State state))
                {
                    foreach(GamepadButtons button in Enum.GetValues(typeof(GamepadButtons)))
                    {
                        if (button is GamepadButtons.None)
                            continue;
                        if(state.Gamepad.Buttons.HasFlag(button))
                        {
                            pressedButton = button;
                            return;
                        }
                    }
                }

                for(uint i =1; i < 0xff; i++)
                {
                    if(NativeKeyboard.IsKeyDown(i))
                    {
                        pressedKey = i;
                        return;
                    }

                }

            }
        }

        public void UpdateControllerBinds(Dictionary<GamepadButtons, InputIntent> controllerBinds)
        {
            this.controllerBinds = controllerBinds;
            keybindsChanged = true;

            Config.current.controllerBinds = controllerBinds;
            Config.SaveConfig();
        }

        public void UpdateKeyboardBinds(Dictionary<uint, InputIntent> keyBinds)
        {
            this.keyBinds = keyBinds;
            keybindsChanged = true;

            Config.current.keyBinds = keyBinds;
            Config.SaveConfig();
        }

        public void UpdateInputBinds(Dictionary<uint,InputIntent> keyBinds, Dictionary<GamepadButtons,InputIntent> controllerBinds)
        {
            this.controllerBinds = controllerBinds;
            this.keyBinds = keyBinds;
            keybindsChanged = true;

            Config.current.keyBinds = keyBinds;
            Config.current.controllerBinds = controllerBinds;
            Config.SaveConfig();
        }

        //To be called from input thread
        private List<InputIntent> GetCurrentIntents(in ImmutableDictionary<uint,InputIntent> ro_KeyBinds, in ImmutableDictionary<GamepadButtons,InputIntent> ro_ControllerBinds)
        {
            List<InputIntent> intents = new List<InputIntent>();

            //Avoid bug in accessibility menu, when enabling key repetition with the left/right inputs
            if(!Config.current.KeyRepetition)
            {
                foreach(var item in keyRepeatTimers)
                    keyRepeatTimers[item.Key] = long.MaxValue;
            }

            foreach (var keybind in ro_KeyBinds)
            {
                bool thisKeyDown = NativeKeyboard.IsKeyDown(keybind.Key);
                if (thisKeyDown && !heldKeys[keybind.Key])
                {
                    if(keyRepeatTimers.ContainsKey(keybind.Value))
                        keyRepeatTimers[keybind.Value] = DateTime.UtcNow.Ticks + initialKeyRepeatDelay*10000;
                    intents.Add(keybind.Value);
                }
                else if(Config.current.KeyRepetition && thisKeyDown && keyRepeatTimers.ContainsKey(keybind.Value))
                {
                    if (keyRepeatTimers[keybind.Value] < DateTime.UtcNow.Ticks)
                    {
                        keyRepeatTimers[keybind.Value] = DateTime.UtcNow.Ticks + keyRepeatDelay * 10000;
                        intents.Add(keybind.Value);
                    }
                }

                heldKeys[keybind.Key] = thisKeyDown;
            }

            if (XInput.GetState(0, out State state))
            {
                foreach (var controllerBind in ro_ControllerBinds)
                {
                    bool shouldPress = state.Gamepad.Buttons.HasFlag(controllerBind.Key) && !prevState.Gamepad.Buttons.HasFlag(controllerBind.Key);

                    if(Config.current.KeyRepetition && keyRepeatTimers.ContainsKey(controllerBind.Value))
                    {
                        if(shouldPress)
                            keyRepeatTimers[controllerBind.Value] = DateTime.UtcNow.Ticks + initialKeyRepeatDelay * 10000;
                        else if (state.Gamepad.Buttons.HasFlag(controllerBind.Key) && keyRepeatTimers[controllerBind.Value] < DateTime.UtcNow.Ticks)
                        {
                            keyRepeatTimers[controllerBind.Value] = DateTime.UtcNow.Ticks + keyRepeatDelay * 10000;
                            shouldPress = true;
                        }
                    }

                    if (shouldPress)
                        intents.Add(controllerBind.Value);
                    
                }

                //TODO: Clean this up
                if (state.Gamepad.LeftThumbX < -xThreshold && prevState.Gamepad.LeftThumbX >= -xThreshold)
                {
                    keyRepeatTimers[InputIntent.Left] = DateTime.UtcNow.Ticks + initialKeyRepeatDelay * 10000;
                    intents.Add(InputIntent.Left);
                }
                else if (state.Gamepad.LeftThumbX < -xThreshold && Config.current.KeyRepetition && keyRepeatTimers[InputIntent.Left] < DateTime.UtcNow.Ticks)
                {
                    keyRepeatTimers[InputIntent.Left] = DateTime.UtcNow.Ticks + keyRepeatDelay * 10000;
                    intents.Add(InputIntent.Left);
                }

                if (state.Gamepad.LeftThumbX > xThreshold && prevState.Gamepad.LeftThumbX <= xThreshold)
                {
                    keyRepeatTimers[InputIntent.Right] = DateTime.UtcNow.Ticks + initialKeyRepeatDelay * 10000;
                    intents.Add(InputIntent.Right);
                }
                else if (state.Gamepad.LeftThumbX > xThreshold && Config.current.KeyRepetition && keyRepeatTimers[InputIntent.Right] < DateTime.UtcNow.Ticks)
                {
                    keyRepeatTimers[InputIntent.Right] = DateTime.UtcNow.Ticks + keyRepeatDelay * 10000;
                    intents.Add(InputIntent.Right);
                }

                if (state.Gamepad.LeftThumbY < -yThreshold && prevState.Gamepad.LeftThumbY >= -yThreshold)
                {
                    keyRepeatTimers[InputIntent.Down] = DateTime.UtcNow.Ticks + initialKeyRepeatDelay * 10000;
                    intents.Add(InputIntent.Down);
                }
                else if (state.Gamepad.LeftThumbY < -yThreshold && Config.current.KeyRepetition && keyRepeatTimers[InputIntent.Down] < DateTime.UtcNow.Ticks)
                {
                    keyRepeatTimers[InputIntent.Down] = DateTime.UtcNow.Ticks + keyRepeatDelay * 10000;
                    intents.Add(InputIntent.Down);
                }

                if (state.Gamepad.LeftThumbY > yThreshold && prevState.Gamepad.LeftThumbY <= yThreshold)
                {
                    keyRepeatTimers[InputIntent.Up] = DateTime.UtcNow.Ticks + initialKeyRepeatDelay * 10000;
                    intents.Add(InputIntent.Up);
                }
                else if (state.Gamepad.LeftThumbY > yThreshold && Config.current.KeyRepetition && keyRepeatTimers[InputIntent.Up] < DateTime.UtcNow.Ticks)
                {
                    keyRepeatTimers[InputIntent.Up] = DateTime.UtcNow.Ticks + keyRepeatDelay * 10000;
                    intents.Add(InputIntent.Up);
                }

            }

            prevState = state;

            return intents;
        }
    }
}
