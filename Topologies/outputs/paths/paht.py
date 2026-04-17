import json
import pickle


def convert_numeric_strings_to_ints(obj):
    """Recursively convert numeric string values to integers."""
    if isinstance(obj, list):
        return [convert_numeric_strings_to_ints(item) for item in obj]
    if isinstance(obj, dict):
        return {k: convert_numeric_strings_to_ints(v) for k, v in obj.items()}
    if isinstance(obj, str):
        try:
            return int(obj)
        except ValueError:
            return obj
    return obj

def json_to_pickle_with_tuple_keys(input_json_path, output_pickle_path):
    """
    Reads a JSON file, converts keys from 'X_Y' to tuple (X, Y),
    and saves the dictionary to a Pickle file.
    """
    try:
        # 1. Read the JSON file
        with open(input_json_path, 'r') as infile:
            data = json.load(infile)

        new_data = {}

        # 2. Iterate and convert keys to actual tuples
        for key, value in data.items():
            parts = key.split('_')

            if len(parts) == 2:
                # Convert the string parts to integers (or leave as strings if preferred)
                # Assuming you want integer tuples like (0, 1) rather than ('0', '1')
                try:
                    tuple_key = (int(parts[0]), int(parts[1]))
                    new_data[tuple_key] = convert_numeric_strings_to_ints(value)
                except ValueError:
                    # Fallback if the parts aren't numbers (e.g., "A_B" -> ("A", "B"))
                    tuple_key = (parts[0], parts[1])
                    new_data[tuple_key] = convert_numeric_strings_to_ints(value)
            else:
                # Keep original key if it doesn't match the expected pattern
                new_data[key] = convert_numeric_strings_to_ints(value)

        # 3. Save the new dictionary to a Pickle file
        with open(output_pickle_path, 'wb') as outfile:
            pickle.dump(new_data, outfile)

        print(f"Successfully processed data and saved to {output_pickle_path}")

        # Optional: Verify it worked by loading it back
        with open(output_pickle_path, 'rb') as verify_file:
            loaded_data = pickle.load(verify_file)
            print(f"Verification: Loaded {len(loaded_data)} items from pickle.")
            # Print the first key to show it's a tuple
            if loaded_data:
                first_key = list(loaded_data.keys())[0]
                print(f"Example key format: {first_key} (Type: {type(first_key)})")

    except FileNotFoundError:
        print(f"Error: The file {input_json_path} was not found.")
    except json.JSONDecodeError:
        print(f"Error: {input_json_path} is not a valid JSON file.")
    except Exception as e:
        print(f"An unexpected error occurred: {e}")

# --- Execute the script ---
input_file = 'b4-teavar_paths.json'
output_file = 'data.pkl'

json_to_pickle_with_tuple_keys(input_file, output_file)