using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using static CSharpTSFInput.NativeMethods;

namespace CSharpTSFInput
{
	[GeneratedComClass]
	public partial class KeyHandlerEditSession : NativeMethods.ITfEditSession
	{
		#region Field

		private readonly NativeMethods.ITfContext Context_Context;
		private readonly TextService Service_Owner;
		private readonly String Str_Composition;
		private readonly Boolean Flag_Commit;
		private readonly Boolean Flag_SelectedCandidateCommit;
		// Set on the asynchronous session a switch-away requests: the composition it was requested for.
		private readonly NativeMethods.ITfComposition? Comp_LeftBehind;

		#endregion Field

		#region Constructor

		public KeyHandlerEditSession (NativeMethods.ITfContext Ptr_Context, TextService Service_Owner, String Str_Text, Boolean Flag_Commit, Boolean Flag_SelectedCandidateCommit = false, NativeMethods.ITfComposition? Comp_LeftBehind = null)
		{
			this.Context_Context = Ptr_Context;
			this.Service_Owner = Service_Owner;
			this.Str_Composition = Str_Text;
			this.Flag_Commit = Flag_Commit;
			this.Flag_SelectedCandidateCommit = Flag_SelectedCandidateCommit;
			this.Comp_LeftBehind = Comp_LeftBehind;
		}

		#endregion Constructor

		#region ITfEditSession

		/// <summary> What actually goes into the composition range.
		///
		/// The composition range carries the composition text and nothing else: no U+200B
		/// prefix (the CUAS workaround, which prepends a zero-width space so the host allocates a text
		/// layout and GetTextExt does not come back TF_E_NOLAYOUT). The displayed text is decided in
		/// this one method.
		///
		/// The defensive ZWSP strippers on the commit paths (KeyHandlerEditSession's final SetText and
		/// TextService's externally-terminated composition) keep a ZWSP arriving from any source out
		/// of the document.</summary>
		public static String ComposeDisplayText (String composition) => composition ?? String.Empty;

		public Int32 DoEditSession (UInt32 Id_Ec)
		{
            NativeMethods.ITfContext context = this.Context_Context;
            if (context == null) return NativeMethods.E_FAIL;
			
			// Composition Management
			if (context is NativeMethods.ITfContextComposition Context_Comp)
			{
				NativeMethods.ITfComposition? Comp_Current = this.Service_Owner.GetComposition ();
				NativeMethods.ITfRange? Range_Comp = null;

				// An asynchronous session runs when the host lets it, possibly after a keystroke's session:
				// TSF processes synchronous sessions before pending asynchronous ones. By then the user may
				// be typing a new word, in a new composition or in this one. The session ends only the
				// composition it was requested for, and only while nothing has been typed into it since.
				if (this.Comp_LeftBehind != null && !this.Service_Owner.IsLeftBehind (this.Comp_LeftBehind))
				{
					return NativeMethods.S_OK;
				}

				// 1. If we are not composing, start a new one
				if (Comp_Current == null && !String.IsNullOrEmpty (this.Str_Composition))
				{
					if (context is NativeMethods.ITfInsertAtSelection Interface_Insert)
					{
						// Manual marshalling: pass IntPtr.Zero for empty string
						int hrInsert = Interface_Insert.InsertTextAtSelection (Id_Ec, NativeMethods.TF_IAS_QUERYONLY, IntPtr.Zero, 0, out Range_Comp);
                        if (hrInsert != NativeMethods.S_OK || Range_Comp == null)
                        {
                            return NativeMethods.E_FAIL;
                        }
					}
					
					if (Range_Comp != null)
					{
						int hrStart = Context_Comp.StartComposition (Id_Ec, Range_Comp, this.Service_Owner, out Comp_Current);
                        if (hrStart != NativeMethods.S_OK || Comp_Current == null)
                        {
                            return hrStart;
                        }
						this.Service_Owner.SetComposition (Comp_Current);

                        // Update Range_Comp to the one owned by the new composition
                        Comp_Current!.GetRange(out Range_Comp);
					}
				}
				else if (Comp_Current != null)
				{
					Comp_Current.GetRange (out Range_Comp);
				}

				// 2. Update Text and Display Attributes
				if (Range_Comp != null && !this.Flag_Commit)
				{
                    String textToDisplay = ComposeDisplayText(this.Str_Composition);
					
					// Manual string marshalling for DisableRuntimeMarshalling compatibility
					IntPtr Ptr_Text = Marshal.StringToCoTaskMemUni (textToDisplay);
					try
					{
						int hrSet = Range_Comp.SetText (Id_Ec, 0, Ptr_Text, (Int32)textToDisplay.Length);
                        if (hrSet != NativeMethods.S_OK)
                        {
                            // Clean up a dangling composition on failure
                            if (Comp_Current != null)
                            {
                                Comp_Current.EndComposition(Id_Ec);
                                this.Service_Owner.SetComposition(null);
                            }
                            return hrSet; // Return error to stop further processing
                        }
					}
					finally
					{
						Marshal.FreeCoTaskMem (Ptr_Text);
					}
					
					// Apply Display Attribute (Underline)
					context!.GetProperty (Globals.Guid_Prop_Attribute, out NativeMethods.ITfProperty? Prop_Attr );
					if (Prop_Attr != null)
					{
						if (this.Service_Owner.TryEnsureDisplayAttributeGuidAtom (out UInt32 atom) && atom != 0)
						{
							ApplyDisplayAttribute(Prop_Attr, Id_Ec, Range_Comp, atom);
						}
					}
					
					// Collapse range to end and update TSF selection (cursor position)
					Range_Comp.Collapse (Id_Ec, 1); // TF_ANCHOR_END
					
					// Explicitly set TSF selection to move the physical cursor
					// Without this call, the editor's caret may not update to the new position.
					// Reference: Weasel Composition.cpp CInlinePreeditEditSession::DoEditSession
					unsafe
					{
						void* pRange = null;
						try
						{
							// Range_Comp is an RCW - we need its underlying native ITfRange pointer
							pRange = ComInterfaceMarshaller<ITfRange>.ConvertToUnmanaged(Range_Comp);
							
							TF_SELECTION tfSelection;
							tfSelection.Range_Range = (nint)pRange;
							tfSelection.Style_Style.Ase_Ase = TfActiveSelEnd.TF_AE_NONE;
							tfSelection.Style_Style.fInterimChar = 0; // FALSE
							
							context.SetSelection(Id_Ec, 1, &tfSelection);
							
						}
						finally
						{
							if (pRange != null)
							{
								ComInterfaceMarshaller<ITfRange>.Free(pRange);
							}
						}
					}

				// 2. Update Candidate Location
				// No GetTextExt call here (it risks a deadlock) and no estimate from the Win32 caret position.
				// Following Weasel: positioning belongs in the read-only EditSession that OnLayoutChange triggers,
				// so the exact coordinates are read after the application has finished laying out.
                }

				// 3. Finalize (Commit)
				if (this.Flag_Commit && Comp_Current != null)
				{
                    // ALWAYS overwrite the composition range with the clean
                    // commit string before EndComposition — even when it's EMPTY.
                    //
                    // On ESC the engine clears its buffer first, so the commit string is "". Skipping the
                    // final SetText for an empty string would leave whatever the range holds, and
                    // EndComposition would finalize it into the document. Setting even "" clears the range
                    // cleanly, and every commit path is uniform, which also overwrites any stray ZWSP
                    // left in the range.
                    {
                        // Defensive: strip any stray leading ZWSP from the commit text itself.
                        string finalText = this.Str_Composition ?? string.Empty;
                        if (finalText.Length > 0 && finalText[0] == '​') finalText = finalText.Substring(1);

                        Comp_Current.GetRange(out NativeMethods.ITfRange? FinalRange);
                        if (FinalRange != null)
                        {
                            IntPtr Ptr_FinalText = Marshal.StringToCoTaskMemUni (finalText);
                            FinalRange.SetText (Id_Ec, 0, Ptr_FinalText, finalText.Length);
                            Marshal.FreeCoTaskMem(Ptr_FinalText);

                            // Move the document caret to the END of what we just
                            // committed. The live-composition branch above does this every keystroke, and the
                            // commit branch must too: on commit most hosts leave the caret after the text
                            // implicitly, but Word snaps it to the composition START (the caret jumps back to the first position). Do
                            // it mechanically: collapse the (now-final-text) range to its end and SetSelection
                            // there (= original start + committed length), BEFORE EndComposition so the host
                            // finalizes with the caret already past the inserted text.
                            FinalRange.Collapse(Id_Ec, 1); // TF_ANCHOR_END
                            unsafe
                            {
                                void* pRange = null;
                                try
                                {
                                    pRange = ComInterfaceMarshaller<ITfRange>.ConvertToUnmanaged(FinalRange);
                                    TF_SELECTION tfCommitSel;
                                    tfCommitSel.Range_Range = (nint)pRange;
                                    tfCommitSel.Style_Style.Ase_Ase = TfActiveSelEnd.TF_AE_NONE;
                                    tfCommitSel.Style_Style.fInterimChar = 0; // FALSE
                                    context.SetSelection(Id_Ec, 1, &tfCommitSel);
                                }
                                finally
                                {
                                    if (pRange != null) ComInterfaceMarshaller<ITfRange>.Free(pRange);
                                }
                            }
                        }
                    }

					Comp_Current.EndComposition (Id_Ec);
					this.Service_Owner.SetComposition (null);
				}
			}
			return NativeMethods.S_OK;
		}

		/// <summary> Gives <paramref name="range"/> the input method's display attribute. GUID_PROP_ATTRIBUTE
		/// takes a VARIANT of type VT_I4 holding the TfGuidAtom registered for Globals.Guid_DisplayAttribute
		/// (<paramref name="atom"/>). Returns SetValue's HRESULT.</summary>
		internal static unsafe Int32 ApplyDisplayAttribute (NativeMethods.ITfProperty property, UInt32 ec, NativeMethods.ITfRange range, UInt32 atom)
		{
			byte* variant = stackalloc byte[24];   // sizeof(VARIANT) on x64
			new Span<byte> (variant, 24).Clear ();
			*(ushort*)variant = NativeMethods.VT_I4;
			*(Int32*)(variant + 8) = (Int32)atom;
			return property.SetValue (ec, range, (nint)variant);
		}

		#endregion ITfEditSession
	}
}
