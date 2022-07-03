using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.ClientState.Buddy;
using Dalamud.Game.Gui;
using Dalamud.Logging;
using Dalamud.Hooking;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Generic;

// using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace GamepadSelection
{
    public class UseActionArgs {
        public IntPtr actionManager;
        public uint actionType;
        public uint actionID;
        public long targetedActorID;
        public uint param;
        public uint useType;
        public int pvp;
        public IntPtr a8;
    }

    public class Member {
        public string Name;
        public uint ID;
        public uint JobID;
    }

    class GamepadSelection : IDisposable
    {
        public static Dictionary<string, ushort> ButtonMap = new Dictionary<string, ushort> {
            {"up", (ushort)GamepadButtons.DpadUp},
            {"down", (ushort)GamepadButtons.DpadDown},
            {"left", (ushort)GamepadButtons.DpadLeft},
            {"right", (ushort)GamepadButtons.DpadRight},

            // Xobx
            {"y", (ushort)GamepadButtons.North},
            {"a", (ushort)GamepadButtons.South},
            {"x", (ushort)GamepadButtons.West},
            {"b", (ushort)GamepadButtons.East},

            // Direction
            {"n", (ushort)GamepadButtons.North},
            {"s", (ushort)GamepadButtons.South},
            {"w", (ushort)GamepadButtons.West},
            {"e", (ushort)GamepadButtons.East},
        };

        private Dictionary<uint, string> actions;
    
        public bool inGamepadSelectionMode = false;

        private int savedButtonsPressed;
        private UseActionArgs gsAction;

        private Configuration Config = Plugin.Config;
        private GamepadState GamepadState = Plugin.GamepadState;
        private PartyList PartyList = Plugin.PartyList;
        private GameGui GameGui = Plugin.GameGui;
        private ClientState ClientState = Plugin.ClientState;
        private ObjectTable Objects = Plugin.Objects;

        public GamepadSelection() {
            this.actions = Plugin.Config.GetActionsInMonitor();
            this.gsAction = new UseActionArgs();

            Plugin.Config.UpdateActionsInMonitor += (actions) => {
                this.actions = actions;
            };

            var Signature = "E8 ?? ?? ?? ?? EB 64 B1 01";
            var useAction = Plugin.SigScanner.ScanText(Signature);
            this.useActionHook = new Hook<UseActionDelegate>(useAction, this.UseActionDetour);

            this.useActionHook.Enable();
        }

        private Hook<UseActionDelegate> useActionHook;
        private delegate byte UseActionDelegate(IntPtr actionManager, uint actionType, uint actionID, long targetedActorID, uint param, uint useType, int pvp, IntPtr a8);
        private byte UseActionDetour(IntPtr actionManager, uint actionType, uint actionID, long targetedActorID, uint param, uint useType, int pvp, IntPtr a8)
        {
            byte ret = 0;
            var a = this.gsAction;
            var pmap = this.GetSortedPartyMembers();
            bool inParty = pmap.Count > 1 || Config.alwaysInParty;  // <---
        
            PluginLog.Debug($"ActionID: {actionID}, SavedActionID: {a.actionID}, TargetID: {targetedActorID}, inGSM: {this.inGamepadSelectionMode}");

            if (this.inGamepadSelectionMode) {
                try {
                    unsafe {
                        var ginput = (GamepadInput*)GamepadState.GamepadInputAddress;

                        // Only use [up down left right y a x b]
                        // 目前GSM只在lt/rt按下, 即激活十字热键栏预备施放技能时可用.
                        int buttons = (ginput->ButtonsPressed & 0x00ff);
                        // 1 0 1 0 Prev
                        // 1 1 0 0 Now
                        // 0 1 0 0 True Buttons
                        // 如果上一次状态和本次相同, 不能判断到底是哪个按键触发了Action.
                        // 多个按键同时按下, 选择优先级高的按键
                        if (buttons != this.savedButtonsPressed)
                            buttons = (buttons ^ this.savedButtonsPressed) & buttons;
                        
                        var order = this.actions[a.actionID].ToLower().Trim().Split(" ").Where((a) => a != "").ToList();
                        var gsTargetedActorIndex = order.FindIndex((b) => ButtonMap.ContainsKey(b) ? (ButtonMap[b] & buttons) > 0 : false);
                        
                        if (pmap.Count > 0) {
                            gsTargetedActorIndex = gsTargetedActorIndex == -1 ? 0 : (gsTargetedActorIndex >= pmap.Count - 1 ? pmap.Count - 1 : gsTargetedActorIndex);
                            
                            var gsTargetedActorID = targetedActorID;
                            if (!pmap.Any(x => x.ID == (uint)targetedActorID)) {   // Disable GSM if we already selected a member.
                                gsTargetedActorID = (long)pmap[gsTargetedActorIndex].ID;
                            }
                            a.targetedActorID = gsTargetedActorID;
                        }
                        
                        PluginLog.Debug($"[Party] ID: {PartyList.PartyId}, Length: {PartyList.Length}, index: {gsTargetedActorIndex}, btn: {Convert.ToString(buttons, 2)}, savedBtn: {Convert.ToString(this.savedButtonsPressed, 2)}, origBtn: {Convert.ToString((ginput->ButtonsPressed & 0x00ff), 2)}, Action: {a.actionID} Target: {a.targetedActorID}");
                    }
                } catch(Exception e) {
                    PluginLog.Error($"Exception: {e}");
                }

                ret = this.useActionHook.Original(a.actionManager, a.actionType, a.actionID, a.targetedActorID, a.param, a.useType, a.pvp, a.a8);
                
                this.savedButtonsPressed = 0;
                this.inGamepadSelectionMode = false;
            } else {
                // Cast normally if:
                //  1. We are not in party
                //  2. We already target a party member
                //  3. Action not in monitor
                if (!inParty || !this.actions.ContainsKey(actionID) || pmap.Any(x => x.ID == (uint)targetedActorID)) {
                    ret = this.useActionHook.Original(actionManager, actionType, actionID, targetedActorID, param, useType, pvp, a8);
                } else {
                    this.gsAction.actionManager = actionManager;
                    this.gsAction.actionType = actionType;
                    this.gsAction.actionID = actionID;
                    this.gsAction.targetedActorID = targetedActorID;
                    this.gsAction.param = param;
                    this.gsAction.useType = useType;
                    this.gsAction.pvp = pvp;
                    this.gsAction.a8 = a8;

                    this.inGamepadSelectionMode = true;
                }
            }
        
            unsafe {
                var ginput = (GamepadInput*)GamepadState.GamepadInputAddress;
                this.savedButtonsPressed = (ushort)(ginput->ButtonsPressed & 0x00ffu);
            }

            return ret;
        }

        private List<Member> GetSortedPartyMembers(string order = "thmr")
        {
            try {
                unsafe {
                    var addonPartyList = (AddonPartyList*)GameGui.GetAddonByName("_PartyList", 1);
                    if (addonPartyList is null)
                        return this.GetDefaultSortedPartyMembers(order);

                    var pmap = new Dictionary<string, PartyMember>();

                    foreach (PartyMember p in PartyList) {
                        var name = p.Name.ToString();
                        pmap.Add(name, p);
                    }

                    uint selfID = 0;
                    uint selfJobID = 0;
                    string selfName = "";
                    
                    if (ClientState.LocalPlayer is not null) {
                        selfID = ClientState.LocalPlayer.ObjectId;
                        selfJobID = ClientState.LocalPlayer.ClassJob.Id;
                        selfName = ClientState.LocalPlayer.Name.ToString();
                    }

                    var me = new List<Member>() {
                        new Member() {Name = selfName, ID = selfID, JobID = selfJobID}
                    };

                    for (var i = 1; i < addonPartyList->MemberCount; i++) {
                        // 90级 玩家2
                        // Lv90 Player Two
                        // 90级 [皇冠]玩家3
                        // 过场动画中
                        var name = addonPartyList->PartyMember[i].Name->NodeText.ToString();
                        // remove non-visible special characters.
                        name = Regex.Replace(name, @"[\x01-\x1F,\x7F]", "");
                        name = name.Split(" ", 2).Last().Trim();
                        me.Add(new Member() {
                            Name = name,
                            ID = pmap.ContainsKey(name) ? pmap[name].ObjectId : 0,
                            JobID = pmap.ContainsKey(name) ? pmap[name].ClassJob.Id : 0,
                        });
                    }

                    var objectMap = new Dictionary<string, uint>();

                    if (addonPartyList->TrustCount > 0) {
                        foreach (var obj in Objects) {
                            var name = obj.Name.ToString().Trim();
                            
                            if (String.IsNullOrEmpty(name)) continue;
                            
                            var id = obj.ObjectId;
                            objectMap.TryAdd(name, id);
                        }
                    }

                    for (var i = 0; i < addonPartyList->TrustCount; i++) {
                        var name = addonPartyList->TrustMember[i].Name->NodeText.ToString()
                                                                                .Split(" ", 2)
                                                                                .Last()
                                                                                .Trim();
                        me.Add(new Member() {
                            Name = name,
                            ID = objectMap.ContainsKey(name) ? objectMap[name] : 0,
                            JobID = 0,  // ignored
                        });
                    }

                    return me;
                }
            } catch (Exception e) {
                PluginLog.Error($"Exception: {e}");

                return new List<Member>();
            }
        }

        // sort order eg: [s] t h m r. always place oneself in the 1st place.
        // ranged dps sort order: r = [pr mr]
        // [H] WHM: 24  SCH: 28 AST: 33 SGE: 40
        // [T] PLD: 19  WAR: 21 DRK: 32 GNB: 37
        // [M] MNK: 20  DRG: 22 NIN: 30 SAM: 34 RPR: 39
        // [PR] BRD: 23 MCH: 31 DNC: 38
        // [MR] BLM: 25 SMN: 27 RDM: 35
        // [DEPRECATED]: We could get the right order from PartyList UI in game.
        private List<Member> GetDefaultSortedPartyMembers(string order = "thmr")
        {
            uint selfID = 0;
            uint selfJobID = 0;
            string selfName = "";
            
            if (ClientState.LocalPlayer is not null) {
                selfID = ClientState.LocalPlayer.ObjectId;
                selfJobID = ClientState.LocalPlayer.ClassJob.Id;
                selfName = ClientState.LocalPlayer.Name.ToString();
            }

            var me = new List<Member>() {
                new Member() {Name = selfName, ID = selfID, JobID = selfJobID}
            };
            
            var t = new List<Member>();
            var h = new List<Member>();
            var m = new List<Member>();
            var pr = new List<Member>();
            var mr = new List<Member>();
            
            foreach (PartyMember p in PartyList) {
                var pid = p.ObjectId;
                if (pid == selfID) continue;
                
                var jobID = p.ClassJob.Id;
                var name = p.Name.ToString();
                
                var member = new Member() {
                    Name = name, ID = pid, JobID = jobID
                };
                
                switch (jobID)
                {
                    case 24:
                    case 33:
                    case 28:
                    case 40:
                        h.Add(member);
                        break;
                    case 19:
                    case 21:
                    case 32:
                    case 37:
                        t.Add(member);
                        break;
                    case 20:
                    case 22:
                    case 30:
                    case 34:
                    case 39:
                        m.Add(member);
                        break;
                    case 23:
                    case 31:
                    case 38:
                        pr.Add(member);
                        break;
                    case 25:
                    case 27:
                    case 35:
                        mr.Add(member);
                        break;
                    default:
                        break;
                }
            }

            t = t.OrderBy(x => x.ID).ThenByDescending(x => x.Name).ToList();
            h = t.OrderBy(x => x.ID).ThenByDescending(x => x.Name).ToList();
            m = t.OrderBy(x => x.ID).ThenByDescending(x => x.Name).ToList();
            pr = t.OrderBy(x => x.ID).ThenByDescending(x => x.Name).ToList();
            mr = t.OrderBy(x => x.ID).ThenByDescending(x => x.Name).ToList();
            
            foreach(char a in order) {
                switch (a)
                {
                    case 't':
                        me.AddRange(t); break;
                    case 'h':
                        me.AddRange(h); break;
                    case 'm':
                        me.AddRange(m); break;
                    case 'r':
                        me.AddRange(pr); me.AddRange(mr); break;
                    default:
                        break;
                }
            }

            return me.ToList();
        }

        public void Dispose()
        {
            this.useActionHook.Disable();
            this.useActionHook.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}